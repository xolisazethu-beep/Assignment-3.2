using CareerHub.Api.Data;
using CareerHub.Api.DTOs;
using CareerHub.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace CareerHub.Api.Repositories;

public class CompanyRepository(CareerHubDbContext db) : ICompanyRepository
{
    public async Task<IReadOnlyList<CompanyResponse>> GetAllAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // Flat projection: the active-listing count is computed in the SELECT so
        // EF emits a single SELECT with a correlated sub-query rather than loading
        // the JobListings navigation collection into memory.
        return await db.Companies
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new CompanyResponse(
                c.Id,
                c.Name,
                c.City,
                c.Province,
                c.Industry,
                c.Website,
                c.FoundedYear,
                c.JobListings.Count(j => j.Status == ListingStatus.Active && j.ExpiresAt > now)))
            .ToListAsync(ct);
    }
}
