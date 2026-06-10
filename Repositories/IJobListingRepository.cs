using CareerHub.Api.DTOs;
using CareerHub.Api.Models;

namespace CareerHub.Api.Repositories;

/// <summary>
/// Read/write access to <see cref="JobListing"/> data. The service layer depends
/// only on this interface — the compiled queries, raw SQL and full-text plumbing
/// in the implementation are invisible here, so adding them never changed a
/// single signature (Assignment 2.4 Part 6 requirement).
/// </summary>
public interface IJobListingRepository
{
    /// <summary>Active, unexpired listings for the public job board. HOT PATH.</summary>
    Task<IReadOnlyList<JobListingResponse>> GetActiveListingsAsync(CancellationToken ct = default);

    /// <summary>
    /// PART 3/4: the paginated, filtered, sorted public job board. Active,
    /// unexpired listings narrowed by the supplied <see cref="JobListingFilterQuery"/>.
    /// Exactly ONE CountAsync and ONE ToListAsync over the same composed IQueryable.
    /// </summary>
    Task<PagedResponse<JobListingResponse>> GetActiveListingsPagedAsync(
        JobListingFilterQuery query, CancellationToken ct = default);

    /// <summary>An employer's own listings (any status).</summary>
    Task<IReadOnlyList<JobListingResponse>> GetByCompanyAsync(Guid companyId, CancellationToken ct = default);

    /// <summary>PART 3/4: an employer's own listings (any status), paginated + filtered + sorted.</summary>
    Task<PagedResponse<JobListingResponse>> GetByCompanyPagedAsync(
        Guid companyId, JobListingFilterQuery query, CancellationToken ct = default);

    /// <summary>
    /// Active, unexpired listings narrowed by any combination of free-text title,
    /// location and job type. All filters are optional; a null/blank filter is
    /// ignored. Used by the public "filter the job board" experience.
    /// </summary>
    Task<IReadOnlyList<JobListingResponse>> BrowseAsync(
        string? title, string? location, JobType? type, CancellationToken ct = default);

    /// <summary>Full detail of one listing for the detail page, or null if not found.</summary>
    Task<JobListingDetailResponse?> GetDetailByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Active, unexpired listings matching a full-text query. Uses the GIN index.</summary>
    Task<IReadOnlyList<JobListingResponse>> SearchAsync(string searchTerm, CancellationToken ct = default);

    /// <summary>Per-status application breakdown + rank for a company's active listings (raw SQL).</summary>
    Task<IReadOnlyList<JobListingStatsResponse>> GetApplicationStatsAsync(Guid companyId, CancellationToken ct = default);

    /// <summary>Fetch a TRACKED listing entity (for writes), or null. Used by apply + patch.</summary>
    Task<JobListing?> GetEntityByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// PART 5A: load the tracked entity via <see cref="GetEntityByIdAsync"/> and
    /// apply only the NON-NULL fields of <paramref name="req"/> to it. Returns the
    /// mutated, still-tracked entity (NOT yet saved) so the service can re-validate
    /// before committing, or null if the listing does not exist.
    /// </summary>
    Task<JobListing?> PatchAsync(Guid id, UpdateJobListingRequest req, CancellationToken ct = default);

    Task AddAsync(JobListing listing, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
