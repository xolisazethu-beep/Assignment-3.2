using CareerHub.Api.DTOs;
using CareerHub.Api.Models;

namespace CareerHub.Api.Services;

public interface IJobService
{
    Task<IReadOnlyList<JobListingResponse>> GetActiveListingsAsync(CancellationToken ct = default);
    Task<PagedResponse<JobListingResponse>> GetActiveListingsPagedAsync(JobListingFilterQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<JobListingResponse>> GetByCompanyAsync(Guid companyId, CancellationToken ct = default);
    Task<PagedResponse<JobListingResponse>> GetByCompanyPagedAsync(Guid companyId, JobListingFilterQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<JobListingResponse>> BrowseAsync(
        string? title, string? location, JobType? type, CancellationToken ct = default);
    Task<JobListingDetailResponse?> GetDetailByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<JobListingResponse>> SearchAsync(string searchTerm, CancellationToken ct = default);
    Task<IReadOnlyList<JobListingStatsResponse>> GetApplicationStatsAsync(Guid companyId, CancellationToken ct = default);
    // companyId comes from the authenticated employer's token, never the request body.
    Task<Guid> CreateAsync(CreateJobListingRequest request, Guid companyId, CancellationToken ct = default);

    /// <summary>PART 5A: partial update of a listing. Throws NotFoundException (404) / ArgumentException (400).</summary>
    Task PatchAsync(Guid id, UpdateJobListingRequest req, CancellationToken ct = default);
}
