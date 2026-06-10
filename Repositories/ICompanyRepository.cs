using CareerHub.Api.DTOs;

namespace CareerHub.Api.Repositories;

/// <summary>
/// Read access to <see cref="Models.Company"/> data. Kept separate from the job
/// repository so each aggregate root owns its own queries (the same separation
/// the job/application repositories already follow).
/// </summary>
public interface ICompanyRepository
{
    /// <summary>All employers, alphabetical, each with its active-listing count.</summary>
    Task<IReadOnlyList<CompanyResponse>> GetAllAsync(CancellationToken ct = default);
}
