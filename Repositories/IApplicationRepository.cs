using CareerHub.Api.DTOs;
using CareerHub.Api.Models;

namespace CareerHub.Api.Repositories;

public interface IApplicationRepository
{
    /// <summary>True if this applicant already applied to this listing. HOT PATH.</summary>
    Task<bool> HasAppliedAsync(Guid applicantId, Guid jobListingId, CancellationToken ct = default);

    /// <summary>All applications for a listing, newest first (employer dashboard).</summary>
    Task<IReadOnlyList<Application>> GetForListingAsync(Guid jobListingId, CancellationToken ct = default);

    /// <summary>
    /// One applicant's own applications + listing/company info ("track applications"),
    /// newest first. When <paramref name="statuses"/> is non-empty the result is
    /// restricted to those internal statuses (used to back the applicant's friendly
    /// <c>?stage=</c> filter).
    /// </summary>
    Task<IReadOnlyList<MyApplicationResponse>> GetByApplicantAsync(
        Guid applicantId, IReadOnlyCollection<Models.ApplicationStatus>? statuses, CancellationToken ct = default);

    /// <summary>
    /// The status of one specific application the applicant submitted, or null if
    /// they never applied to that listing. Backs "track a single job".
    /// </summary>
    Task<MyApplicationStatusResponse?> GetByApplicantAndListingAsync(
        Guid applicantId, Guid jobListingId, CancellationToken ct = default);

    /// <summary>Per-status counts for one applicant — feeds the applicant's stage summary.</summary>
    Task<IReadOnlyDictionary<Models.ApplicationStatus, int>> GetStatusCountsForApplicantAsync(
        Guid applicantId, CancellationToken ct = default);

    /// <summary>
    /// Employer-side: search the applicants who applied to a given company's
    /// listings, filtered by qualification/experience/city (and optionally one
    /// listing). Returns the page of matches and the total match count. Always
    /// scoped to <paramref name="companyId"/> so an employer only ever sees their
    /// own candidate pool.
    /// </summary>
    Task<(IReadOnlyList<ApplicantSearchResponse> Data, int Total)> SearchApplicantsForCompanyAsync(
        Guid companyId, ApplicantSearchQuery query, CancellationToken ct = default);

    /// <summary>
    /// Fetch a single TRACKED application by its composite key (JobListingId,
    /// ApplicantId), or null. Backs the Part 5B status transition and the Part 7
    /// single-resource GET.
    /// </summary>
    Task<Application?> GetTrackedAsync(Guid jobListingId, Guid applicantId, CancellationToken ct = default);

    Task AddAsync(Application application, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
