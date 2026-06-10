using CareerHub.Api.DTOs;
using CareerHub.Api.Infrastructure;
using CareerHub.Api.Models;

namespace CareerHub.Api.Services;

public interface IApplicationService
{
    /// <summary>Apply the given applicant to a listing. Returns nothing; throws on conflict/404.</summary>
    Task ApplyAsync(Guid applicantId, Guid jobListingId, string? coverNote, CancellationToken ct = default);

    /// <summary>
    /// The applicant's own application history, newest first. When
    /// <paramref name="stage"/> is supplied only applications in that friendly
    /// stage (Applied/Pending/Accepted/Rejected) are returned.
    /// </summary>
    Task<IReadOnlyList<MyApplicationResponse>> GetMineAsync(
        Guid applicantId, ApplicationStage? stage = null, CancellationToken ct = default);

    /// <summary>The status/stage of one specific application the applicant submitted, or null.</summary>
    Task<MyApplicationStatusResponse?> GetMineStatusAsync(
        Guid applicantId, Guid jobListingId, CancellationToken ct = default);

    /// <summary>A per-stage count summary of the applicant's whole pipeline.</summary>
    Task<MyApplicationsSummaryResponse> GetMineSummaryAsync(Guid applicantId, CancellationToken ct = default);

    /// <summary>
    /// Employer-side applicant search: the candidates who applied to the given
    /// company's listings, filtered by qualification/experience/city. Returns the
    /// page of matches plus the total count for the paging envelope.
    /// </summary>
    Task<(IReadOnlyList<ApplicantSearchResponse> Data, int Total)> SearchApplicantsAsync(
        Guid companyId, ApplicantSearchQuery query, CancellationToken ct = default);

    /// <summary>PART 7: a single application by composite key, or null. For the ETag GET.</summary>
    Task<ApplicationResponse?> GetAsync(Guid jobListingId, Guid applicantId, CancellationToken ct = default);

    /// <summary>
    /// PART 5B: move an application to a new status, enforcing the legal-transition
    /// state machine. Throws NotFoundException (404) if it does not exist, or
    /// ArgumentException (400) for an illegal transition.
    /// </summary>
    Task UpdateStatusAsync(Guid jobListingId, Guid applicantId, ApplicationStatus newStatus, CancellationToken ct = default);
}
