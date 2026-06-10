using CareerHub.Api.Data;
using CareerHub.Api.DTOs;
using CareerHub.Api.Infrastructure;
using CareerHub.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace CareerHub.Api.Repositories;

public class ApplicationRepository(CareerHubDbContext db) : IApplicationRepository
{
    // ── PART 6: COMPILED QUERY — HasAppliedAsync ─────────────────────────────
    // HOT PATH. Runs on EVERY application submission (the duplicate-application
    // guard before the INSERT) and again whenever a listing page is rendered for
    // a signed-in seeker to show the "Already applied" badge. It is a tiny,
    // unchanging, high-frequency point lookup — the ideal compiled-query
    // candidate (see README "Hot path justification"). Compiling removes the
    // expression-tree build from a query that fires thousands of times an hour.
    // Hits ix_applications_applicantid_joblistingid.
    private static readonly Func<CareerHubDbContext, Guid, Guid, Task<bool>>
        HasAppliedQuery = EF.CompileAsyncQuery(
            (CareerHubDbContext ctx, Guid applicantId, Guid jobListingId) =>
                ctx.Applications.Any(a => a.ApplicantId == applicantId
                                          && a.JobListingId == jobListingId));

    public Task<bool> HasAppliedAsync(Guid applicantId, Guid jobListingId, CancellationToken ct = default) =>
        HasAppliedQuery(db, applicantId, jobListingId);

    public async Task<IReadOnlyList<Application>> GetForListingAsync(Guid jobListingId, CancellationToken ct = default)
    {
        // Hits ix_applications_joblistingid_submittedat — already ordered, no Sort.
        return await db.Applications
            .AsNoTracking()
            .Where(a => a.JobListingId == jobListingId)
            .OrderByDescending(a => a.SubmittedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MyApplicationResponse>> GetByApplicantAsync(
        Guid applicantId, IReadOnlyCollection<ApplicationStatus>? statuses, CancellationToken ct = default)
    {
        // Hits ix_applications_applicantid_joblistingid (applicant leads). Flat
        // projection across the listing + company so EF emits one SELECT and we
        // never serialise entity graphs — same pattern as the read-side job queries.
        var query = db.Applications
            .AsNoTracking()
            .Where(a => a.ApplicantId == applicantId);

        // Friendly-stage filter: an applicant's ?stage=Pending becomes WHERE
        // Status IN ('UnderReview','Shortlisted'). Skipped when no stage was asked for.
        if (statuses is { Count: > 0 })
            query = query.Where(a => statuses.Contains(a.Status));

        // Map the internal status to the friendly Stage in memory — the switch the
        // mapper uses isn't worth pushing into SQL, and the page is tiny.
        var rows = await query
            .OrderByDescending(a => a.SubmittedAt)
            .Select(a => new
            {
                a.JobListingId,
                JobTitle = a.JobListing.Title,
                CompanyName = a.JobListing.Company.Name,
                a.Status,
                a.SubmittedAt
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new MyApplicationResponse(
                r.JobListingId, r.JobTitle, r.CompanyName,
                r.Status.ToString(), ApplicationStageMapper.ToStage(r.Status).ToString(), r.SubmittedAt))
            .ToList();
    }

    public async Task<MyApplicationStatusResponse?> GetByApplicantAndListingAsync(
        Guid applicantId, Guid jobListingId, CancellationToken ct = default)
    {
        var row = await db.Applications
            .AsNoTracking()
            .Where(a => a.ApplicantId == applicantId && a.JobListingId == jobListingId)
            .Select(a => new
            {
                a.JobListingId,
                JobTitle = a.JobListing.Title,
                CompanyName = a.JobListing.Company.Name,
                a.Status,
                a.SubmittedAt
            })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? null
            : new MyApplicationStatusResponse(
                row.JobListingId, row.JobTitle, row.CompanyName,
                row.Status.ToString(), ApplicationStageMapper.ToStage(row.Status).ToString(), row.SubmittedAt);
    }

    public async Task<IReadOnlyDictionary<ApplicationStatus, int>> GetStatusCountsForApplicantAsync(
        Guid applicantId, CancellationToken ct = default)
    {
        var grouped = await db.Applications
            .AsNoTracking()
            .Where(a => a.ApplicantId == applicantId)
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return grouped.ToDictionary(g => g.Status, g => g.Count);
    }

    public async Task<(IReadOnlyList<ApplicantSearchResponse> Data, int Total)> SearchApplicantsForCompanyAsync(
        Guid companyId, ApplicantSearchQuery query, CancellationToken ct = default)
    {
        // The candidate pool: applications to THIS company's listings only — the
        // server-side scope that stops one employer browsing another's applicants.
        // Optionally narrow to a single one of the employer's listings.
        var companyApplications = db.Applications
            .AsNoTracking()
            .Where(a => a.JobListing.CompanyId == companyId);

        if (query.JobListingId is { } listingId)
            companyApplications = companyApplications.Where(a => a.JobListingId == listingId);

        // Root the search on Applicants so the profile filters are plain Wheres and
        // the per-applicant aggregates are correlated subqueries EF can translate.
        var candidates = db.Applicants
            .AsNoTracking()
            .Where(ap => companyApplications.Any(a => a.ApplicantId == ap.Id));

        if (!string.IsNullOrWhiteSpace(query.Qualification))
        {
            var term = $"%{query.Qualification.Trim()}%";
            candidates = candidates.Where(ap =>
                EF.Functions.ILike(ap.Qualifications, term) || EF.Functions.ILike(ap.Headline, term));
        }

        if (query.MinExperience is { } minExperience)
            candidates = candidates.Where(ap => ap.YearsOfExperience >= minExperience);

        if (!string.IsNullOrWhiteSpace(query.City))
            candidates = candidates.Where(ap => EF.Functions.ILike(ap.City, $"%{query.City.Trim()}%"));

        var total = await candidates.CountAsync(ct);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var rows = await candidates
            .OrderByDescending(ap => ap.YearsOfExperience)
            .ThenBy(ap => ap.FullName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(ap => new
            {
                Applicant = ap,
                ApplicationsToYourCompany = companyApplications.Count(a => a.ApplicantId == ap.Id),
                LatestAppliedAt = companyApplications
                    .Where(a => a.ApplicantId == ap.Id).Max(a => a.SubmittedAt),
                LatestStatus = companyApplications
                    .Where(a => a.ApplicantId == ap.Id)
                    .OrderByDescending(a => a.SubmittedAt)
                    .Select(a => a.Status)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        var data = rows
            .Select(r => new ApplicantSearchResponse(
                r.Applicant.Id, r.Applicant.FullName, r.Applicant.Email, r.Applicant.City,
                r.Applicant.Headline, r.Applicant.YearsOfExperience, r.Applicant.Qualifications,
                r.ApplicationsToYourCompany,
                ApplicationStageMapper.ToStage(r.LatestStatus).ToString(), r.LatestAppliedAt))
            .ToList();

        return (data, total);
    }

    public Task<Application?> GetTrackedAsync(Guid jobListingId, Guid applicantId, CancellationToken ct = default) =>
        db.Applications.FirstOrDefaultAsync(
            a => a.JobListingId == jobListingId && a.ApplicantId == applicantId, ct);

    public async Task AddAsync(Application application, CancellationToken ct = default) =>
        await db.Applications.AddAsync(application, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
