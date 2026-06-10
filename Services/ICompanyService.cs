using CareerHub.Api.DTOs;

namespace CareerHub.Api.Services;

public interface ICompanyService
{
    Task<IReadOnlyList<CompanyResponse>> GetAllAsync(CancellationToken ct = default);
}
