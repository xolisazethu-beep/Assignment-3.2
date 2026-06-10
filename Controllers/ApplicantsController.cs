using Asp.Versioning;
using CareerHub.Api.DTOs;
using CareerHub.Api.Infrastructure;
using CareerHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CareerHub.Api.Controllers;

/// <summary>
/// Employer-side candidate search. Everything here is Employer-only and is scoped
/// server-side to the caller's OWN company (the companyId comes from the JWT, never
/// a query param), so a recruiter can only ever search the people who applied to
/// THEIR roles — never browse another company's candidate pool. Same security
/// pattern as the Part 8 statistics endpoint.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
[Authorize(Roles = "Employer")]
[Tags("Applicants")] // OpenAPI grouping
public class ApplicantsController(IApplicationService applications) : ControllerBase
{
    /// <summary>
    /// Search the applicants who applied to your company's listings, filtered by
    /// qualification, minimum years of experience, city and/or a specific listing
    /// of yours. <c>qualification</c> is a case-insensitive substring match over the
    /// applicant's Qualifications and Headline. Paginated (page/pageSize, pageSize
    /// clamped to ≤ 100); writes the total match count to <c>X-Total-Count</c> and
    /// returns the same HATEOAS-lite paging envelope as the job board.
    /// </summary>
    /// <example>GET /api/v1/applicants/search?qualification=kubernetes&amp;minExperience=5</example>
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] ApplicantSearchQuery query, CancellationToken ct)
    {
        query.PageSize = Math.Clamp(query.PageSize, 1, 100);
        query.Page = Math.Max(1, query.Page);

        var (data, total) = await applications.SearchApplicantsAsync(User.GetCompanyId(), query, ct);

        var page = PagedResponse<ApplicantSearchResponse>.Create(data, query.Page, query.PageSize, total);
        Response.Headers["X-Total-Count"] = total.ToString();
        return Ok(page with { Links = PaginationLinks.Build(Request, page) });
    }
}
