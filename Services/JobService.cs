using CareerHub.Api.DTOs;
using CareerHub.Api.Exceptions;
using CareerHub.Api.Models;
using CareerHub.Api.Repositories;

namespace CareerHub.Api.Services;

/// <summary>
/// Business-rule layer. It validates salary ranges and expiry before a write —
/// but note (Part 1) this validation lives in C# and is bypassed by any writer
/// that does not go through this method, which is precisely why the same rules
/// are ALSO check constraints in the database.
/// </summary>
public class JobService(IJobListingRepository jobs) : IJobService
{
    public Task<IReadOnlyList<JobListingResponse>> GetActiveListingsAsync(CancellationToken ct = default) =>
        jobs.GetActiveListingsAsync(ct);

    public Task<PagedResponse<JobListingResponse>> GetActiveListingsPagedAsync(JobListingFilterQuery query, CancellationToken ct = default) =>
        jobs.GetActiveListingsPagedAsync(query, ct);

    public Task<IReadOnlyList<JobListingResponse>> GetByCompanyAsync(Guid companyId, CancellationToken ct = default) =>
        jobs.GetByCompanyAsync(companyId, ct);

    public Task<PagedResponse<JobListingResponse>> GetByCompanyPagedAsync(Guid companyId, JobListingFilterQuery query, CancellationToken ct = default) =>
        jobs.GetByCompanyPagedAsync(companyId, query, ct);

    public Task<IReadOnlyList<JobListingResponse>> BrowseAsync(
        string? title, string? location, JobType? type, CancellationToken ct = default) =>
        jobs.BrowseAsync(title, location, type, ct);

    public Task<JobListingDetailResponse?> GetDetailByIdAsync(Guid id, CancellationToken ct = default) =>
        jobs.GetDetailByIdAsync(id, ct);

    public Task<IReadOnlyList<JobListingResponse>> SearchAsync(string searchTerm, CancellationToken ct = default) =>
        jobs.SearchAsync(searchTerm, ct);

    public Task<IReadOnlyList<JobListingStatsResponse>> GetApplicationStatsAsync(Guid companyId, CancellationToken ct = default) =>
        jobs.GetApplicationStatsAsync(companyId, ct);

    public async Task<Guid> CreateAsync(CreateJobListingRequest request, Guid companyId, CancellationToken ct = default)
    {
        if (request.SalaryMin is <= 0)
            throw new ArgumentException("SalaryMin must be greater than zero.");
        if (request.SalaryMin is not null && request.SalaryMax is not null && request.SalaryMax <= request.SalaryMin)
            throw new ArgumentException("SalaryMax must be greater than SalaryMin.");
        if (request.ExpiresAt <= DateTime.UtcNow)
            throw new ArgumentException("ExpiresAt must be in the future.");

        var listing = new JobListing
        {
            Id = Guid.NewGuid(),
            Title = request.Title,
            Description = request.Description,
            MinimumRequirements = request.MinimumRequirements,
            Location = request.Location,
            Type = request.Type,
            SalaryMin = request.SalaryMin,
            SalaryMax = request.SalaryMax,
            Status = ListingStatus.Active,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = request.ExpiresAt,
            CompanyId = companyId
        };

        await jobs.AddAsync(listing, ct);
        await jobs.SaveChangesAsync(ct);
        return listing.Id;
    }

    // ── PART 5A: PATCH ───────────────────────────────────────────────────────
    // The repository fetches the tracked entity and applies the non-null fields;
    // here we re-run the SAME validation rules as CreateAsync, but ONLY for the
    // fields the request actually touched, then commit.
    public async Task PatchAsync(Guid id, UpdateJobListingRequest req, CancellationToken ct = default)
    {
        var listing = await jobs.PatchAsync(id, req, ct)
            ?? throw new NotFoundException("That job listing does not exist.");

        // Re-run the salary-range check only if either salary field was provided.
        if (req.SalaryMin is not null || req.SalaryMax is not null)
        {
            if (listing.SalaryMin is <= 0)
                throw new ArgumentException("SalaryMin must be greater than zero.");
            if (listing.SalaryMin is not null && listing.SalaryMax is not null && listing.SalaryMax <= listing.SalaryMin)
                throw new ArgumentException("SalaryMax must be greater than SalaryMin.");
        }

        // Re-run the expiry check only if ExpiresAt was provided.
        if (req.ExpiresAt is not null && listing.ExpiresAt <= DateTime.UtcNow)
            throw new ArgumentException("ExpiresAt must be in the future.");

        await jobs.SaveChangesAsync(ct);
    }
}
