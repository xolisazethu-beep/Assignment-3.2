using Asp.Versioning;
using CareerHub.Api.DTOs;
using CareerHub.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace CareerHub.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
[Tags("Companies")]
public class CompaniesController(ICompanyService companies) : ControllerBase
{
    /// <summary>All employers, with each one's active-listing count.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<CompanyResponse>> GetAll(CancellationToken ct)
        => await companies.GetAllAsync(ct);
}
