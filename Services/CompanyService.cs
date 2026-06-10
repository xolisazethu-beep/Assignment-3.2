using CareerHub.Api.DTOs;
using CareerHub.Api.Repositories;

namespace CareerHub.Api.Services;

/// <summary>
/// Companies are reference data with no write-side rules in this assignment, so
/// the service is a thin pass-through to the repository — present for symmetry
/// with <see cref="JobService"/> and so a future rule has an obvious home.
/// </summary>
public class CompanyService(ICompanyRepository companies) : ICompanyService
{
    public Task<IReadOnlyList<CompanyResponse>> GetAllAsync(CancellationToken ct = default) =>
        companies.GetAllAsync(ct);
}
