using System.Linq.Expressions;
using CareerHub.Api.Data;
using CareerHub.Api.DTOs;
using CareerHub.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace CareerHub.Api.Repositories;

public class JobListingRepository(CareerHubDbContext db) : IJobListingRepository
{
    // ── SHARED READ-SIDE PROJECTION ──────────────────────────────────────────
    // One definition of "what a listing looks like to a client", reused by the
    // company and search queries so the SELECT column list never drifts apart.
    private static readonly Expression<Func<JobListing, JobListingResponse>> Project = j =>
        new JobListingResponse(
            j.Id, j.Title, j.Location, j.Type.ToString(),
            j.SalaryMin, j.SalaryMax, j.Status.ToString(),
            j.CreatedAt, j.ExpiresAt,
            j.CompanyId, j.Company.Name, j.Company.City);

    // ── PART 6: COMPILED QUERY — GetActiveListingsAsync ──────────────────────
    // HOT PATH. This is the public job board's landing query: it runs on every
    // visit to the home page and every "browse all jobs" pagination request — by
    // far the highest-frequency query in CareerHub (see README "Hot path
    // justification"). Compiling it once removes the LINQ-expression-tree parse
    // and SQL-generation cost from every single one of those calls, which is the
    // exact situation EF.CompileAsyncQuery exists for. `now` is a parameter, not
    // a captured constant, so each call still filters against the current instant.
    private static readonly Func<CareerHubDbContext, DateTime, IAsyncEnumerable<JobListingResponse>>
        ActiveListingsQuery = EF.CompileAsyncQuery(
            (CareerHubDbContext ctx, DateTime now) =>
                ctx.JobListings
                   .AsNoTracking()
                   .Where(j => j.Status == ListingStatus.Active && j.ExpiresAt > now)
                   .OrderByDescending(j => j.CreatedAt)
                   .Select(j => new JobListingResponse(
                       j.Id, j.Title, j.Location, j.Type.ToString(),
                       j.SalaryMin, j.SalaryMax, j.Status.ToString(),
                       j.CreatedAt, j.ExpiresAt,
                       j.CompanyId, j.Company.Name, j.Company.City)));

    public async Task<IReadOnlyList<JobListingResponse>> GetActiveListingsAsync(CancellationToken ct = default)
    {
        var results = new List<JobListingResponse>();
        await foreach (var row in ActiveListingsQuery(db, DateTime.UtcNow).WithCancellation(ct))
            results.Add(row);
        return results;
    }

    // ── PART 3/4: PAGINATED + FILTERED + SORTED PUBLIC BOARD ─────────────────
    public Task<PagedResponse<JobListingResponse>> GetActiveListingsPagedAsync(
        JobListingFilterQuery query, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var q = db.JobListings
            .AsNoTracking()
            .Where(j => j.Status == ListingStatus.Active && j.ExpiresAt > now);
        return PageAsync(ApplyFilters(q, query), query, ct);
    }

    // ── PART 3/4: PAGINATED COMPANY LISTINGS (any status) ────────────────────
    public Task<PagedResponse<JobListingResponse>> GetByCompanyPagedAsync(
        Guid companyId, JobListingFilterQuery query, CancellationToken ct = default)
    {
        var q = db.JobListings
            .AsNoTracking()
            .Where(j => j.CompanyId == companyId);
        return PageAsync(ApplyFilters(q, query), query, ct);
    }

    // ── PART 4: COMPOSABLE FILTERS ───────────────────────────────────────────
    // Each supplied filter narrows the IQueryable with an additional Where (AND
    // semantics); absent (null) filters are skipped so they never reach the SQL.
    private static IQueryable<JobListing> ApplyFilters(IQueryable<JobListing> q, JobListingFilterQuery f)
    {
        // EXTRA "q": reuse the 2.4 GIN-indexed full-text search when present, then
        // compose the rest of the filters on top of it (Bitmap-AND in Postgres).
        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            var words = f.Q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var tsQuery = string.Join(" & ", words);
            q = q.Where(j => j.SearchVector.Matches(EF.Functions.ToTsQuery("english", tsQuery)));
        }

        if (!string.IsNullOrWhiteSpace(f.Location))
            q = q.Where(j => EF.Functions.ILike(j.Location, $"%{f.Location}%"));

        if (f.EmploymentType is not null)
            q = q.Where(j => j.Type == f.EmploymentType);

        // SalaryMin = "pays at least X": the top of the listing's range must reach
        // it (fall back to the floor when no max is stated). SalaryMax = "pays at
        // most Y": the bottom of the range must not exceed it.
        if (f.SalaryMin is not null)
            q = q.Where(j => (j.SalaryMax ?? j.SalaryMin) >= f.SalaryMin);
        if (f.SalaryMax is not null)
            q = q.Where(j => (j.SalaryMin ?? j.SalaryMax) <= f.SalaryMax);

        if (f.CompanyId is not null)
            q = q.Where(j => j.CompanyId == f.CompanyId);

        // EXTRA "postedSince": only listings created on or after the given instant.
        if (f.PostedSince is not null)
            q = q.Where(j => j.CreatedAt >= f.PostedSince);

        // EXTRA "remoteOnly": case-insensitive contains "remote" on Location.
        if (f.RemoteOnly == true)
            q = q.Where(j => EF.Functions.ILike(j.Location, "%remote%"));

        return q;
    }

    // ── PART 4: SORT via SWITCH EXPRESSION ───────────────────────────────────
    // Maps sort key + direction to OrderBy/OrderByDescending. The default case is
    // ALWAYS PostedAt (CreatedAt) DESC. OrderBy is applied here, BEFORE Skip/Take.
    private static IQueryable<JobListing> ApplySort(IQueryable<JobListing> q, JobListingFilterQuery f)
    {
        var asc = string.Equals(f.Dir, "asc", StringComparison.OrdinalIgnoreCase);
        return f.Sort?.ToLowerInvariant() switch
        {
            "salarymin" => asc ? q.OrderBy(j => j.SalaryMin) : q.OrderByDescending(j => j.SalaryMin),
            "salarymax" => asc ? q.OrderBy(j => j.SalaryMax) : q.OrderByDescending(j => j.SalaryMax),
            "title"     => asc ? q.OrderBy(j => j.Title) : q.OrderByDescending(j => j.Title),
            // EXTRA "company": always ascending by company name.
            "company"   => q.OrderBy(j => j.Company.Name),
            // EXTRA "expiresat": ascending = expiring-soonest first.
            "expiresat" => q.OrderBy(j => j.ExpiresAt),
            // EXTRA "relevance": with no rank surface available we fall back to
            // PostedAt DESC (results are already narrowed by the full-text filter).
            "relevance" => q.OrderByDescending(j => j.CreatedAt),
            "postedat"  => asc ? q.OrderBy(j => j.CreatedAt) : q.OrderByDescending(j => j.CreatedAt),
            _           => q.OrderByDescending(j => j.CreatedAt) // default: PostedAt DESC
        };
    }

    // ── PART 3: SINGLE-PASS PAGINATION ───────────────────────────────────────
    // Exactly ONE CountAsync (the full match count) and ONE ToListAsync (the
    // page), both over the same composed IQueryable. OrderBy precedes Skip/Take.
    private static async Task<PagedResponse<JobListingResponse>> PageAsync(
        IQueryable<JobListing> filtered, JobListingFilterQuery f, CancellationToken ct)
    {
        var page = f.Page < 1 ? 1 : f.Page;
        var pageSize = Math.Clamp(f.PageSize, 1, 100);

        var totalCount = await filtered.CountAsync(ct);

        var data = await ApplySort(filtered, f)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(Project)
            .ToListAsync(ct);

        return PagedResponse<JobListingResponse>.Create(data, page, pageSize, totalCount);
    }

    public async Task<IReadOnlyList<JobListingResponse>> GetByCompanyAsync(Guid companyId, CancellationToken ct = default)
    {
        // Hits ix_job_listings_companyid_status (CompanyId leads, equality first).
        return await db.JobListings
            .AsNoTracking()
            .Where(j => j.CompanyId == companyId)
            .OrderByDescending(j => j.CreatedAt)
            .Select(Project)
            .ToListAsync(ct);
    }

    // ── FILTERED BROWSE: title / location / type ─────────────────────────────
    // Composable IQueryable: each supplied filter narrows the query; absent ones
    // are skipped so they don't appear in the generated SQL at all. The Status +
    // ExpiresAt predicate still rides ix_job_listings_status_expiresat. ILike is a
    // PostgreSQL case-insensitive LIKE — exact substring matching, distinct from
    // the stemmed full-text SearchAsync.
    public async Task<IReadOnlyList<JobListingResponse>> BrowseAsync(
        string? title, string? location, JobType? type, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var query = db.JobListings
            .AsNoTracking()
            .Where(j => j.Status == ListingStatus.Active && j.ExpiresAt > now);

        if (!string.IsNullOrWhiteSpace(title))
            query = query.Where(j => EF.Functions.ILike(j.Title, $"%{title}%"));

        if (!string.IsNullOrWhiteSpace(location))
            query = query.Where(j => EF.Functions.ILike(j.Location, $"%{location}%"));

        if (type is not null)
            query = query.Where(j => j.Type == type);

        return await query
            .OrderByDescending(j => j.CreatedAt)
            .Select(Project)
            .ToListAsync(ct);
    }

    public Task<JobListingDetailResponse?> GetDetailByIdAsync(Guid id, CancellationToken ct = default) =>
        db.JobListings
            .AsNoTracking()
            .Where(j => j.Id == id)
            .Select(j => new JobListingDetailResponse(
                j.Id, j.Title, j.Description, j.MinimumRequirements, j.Location, j.Type.ToString(),
                j.SalaryMin, j.SalaryMax, j.Status.ToString(),
                j.CreatedAt, j.ExpiresAt,
                j.CompanyId, j.Company.Name, j.Company.City, j.Company.Province, j.Company.Website))
            .FirstOrDefaultAsync(ct);

    // ── PART 5: FULL-TEXT SEARCH ─────────────────────────────────────────────
    public async Task<IReadOnlyList<JobListingResponse>> SearchAsync(string searchTerm, CancellationToken ct = default)
    {
        // Build a tsquery that ANDs the user's words: "site reliability" -> 'site & reliability'.
        // ToTsQuery applies the 'english' configuration, so it stems too — a search
        // for "sprint" matches listings containing "sprinting" (LIKE cannot do this).
        var words = searchTerm.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tsQuery = string.Join(" & ", words);
        var now = DateTime.UtcNow;

        // Matching against the STORED SearchVector column (not a per-row to_tsvector
        // call) is what lets PostgreSQL use the GIN index — verified by EXPLAIN
        // ANALYZE showing a Bitmap Index Scan on ix_job_listings_search_vector.
        return await db.JobListings
            .AsNoTracking()
            .Where(j => j.Status == ListingStatus.Active
                        && j.ExpiresAt > now
                        && j.SearchVector.Matches(EF.Functions.ToTsQuery("english", tsQuery)))
            .Select(Project)
            .ToListAsync(ct);
    }

    // ── PART 8: RAW SQL WITH RANK() WINDOW FUNCTION ──────────────────────────
    public async Task<IReadOnlyList<JobListingStatsResponse>> GetApplicationStatsAsync(Guid companyId, CancellationToken ct = default)
    {
        // EF Core's LINQ provider cannot translate RANK() OVER (...) nor the
        // COUNT(*) FILTER (WHERE ...) conditional aggregation, so this is raw SQL.
        // {companyId} is interpolated into a FormattableString: EF turns it into a
        // parameterised @p0 placeholder — it is NEVER concatenated into the text,
        // so it cannot be a SQL-injection vector (see README "FromSql parameterisation").
        FormattableString sql = $"""
            SELECT
                j."Id"                                              AS "JobListingId",
                j."Title"                                           AS "Title",
                COUNT(a."ApplicantId")                              AS "TotalApplications",
                COUNT(*) FILTER (WHERE a."Status" = 'Submitted')    AS "Submitted",
                COUNT(*) FILTER (WHERE a."Status" = 'UnderReview')  AS "UnderReview",
                COUNT(*) FILTER (WHERE a."Status" = 'Shortlisted')  AS "Shortlisted",
                COUNT(*) FILTER (WHERE a."Status" = 'Rejected')     AS "Rejected",
                COUNT(*) FILTER (WHERE a."Status" = 'Offered')      AS "Offered",
                RANK() OVER (ORDER BY COUNT(a."ApplicantId") DESC)  AS "Rank"
            FROM job_listings j
            LEFT JOIN applications a ON a."JobListingId" = j."Id"
            WHERE j."CompanyId" = {companyId} AND j."Status" = 'Active'
            GROUP BY j."Id", j."Title"
            ORDER BY "Rank", j."Title"
            """;

        return await db.Database
            .SqlQuery<JobListingStatsResponse>(sql)
            .ToListAsync(ct);
    }

    public Task<JobListing?> GetEntityByIdAsync(Guid id, CancellationToken ct = default) =>
        db.JobListings.FirstOrDefaultAsync(j => j.Id == id, ct);

    // ── PART 5A: PATCH — apply only non-null fields to the tracked entity ─────
    public async Task<JobListing?> PatchAsync(Guid id, UpdateJobListingRequest req, CancellationToken ct = default)
    {
        var listing = await GetEntityByIdAsync(id, ct);
        if (listing is null)
            return null;

        if (req.Title is not null) listing.Title = req.Title;
        if (req.Description is not null) listing.Description = req.Description;
        if (req.Location is not null) listing.Location = req.Location;
        if (req.EmploymentType is not null) listing.Type = req.EmploymentType.Value;
        if (req.SalaryMin is not null) listing.SalaryMin = req.SalaryMin;
        if (req.SalaryMax is not null) listing.SalaryMax = req.SalaryMax;
        if (req.ExpiresAt is not null) listing.ExpiresAt = req.ExpiresAt.Value;

        return listing; // still tracked; the service validates then SaveChangesAsync
    }

    public async Task AddAsync(JobListing listing, CancellationToken ct = default) =>
        await db.JobListings.AddAsync(listing, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
