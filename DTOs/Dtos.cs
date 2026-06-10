using CareerHub.Api.Models;

namespace CareerHub.Api.DTOs;

/// <summary>
/// The shape returned by the read-side job queries (GetActiveListingsAsync,
/// SearchAsync, GetByCompanyAsync). A flat projection — no navigation graphs —
/// so EF Core emits a single SELECT and we never serialise the whole entity.
/// Salaries are ZAR.
/// </summary>
public record JobListingResponse(
    Guid Id,
    string Title,
    string Location,
    string Type,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string Status,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    Guid CompanyId,
    string CompanyName,
    string CompanyCity);

/// <summary>
/// The full detail of a single listing (GET /api/jobs/{id}). Unlike the lean list
/// projection above, this includes the long-form <see cref="Description"/> and
/// <see cref="MinimumRequirements"/> — the heavy text fields a board view omits but
/// a detail page needs. Still a flat projection: one SELECT, no entity graph.
/// </summary>
public record JobListingDetailResponse(
    Guid Id,
    string Title,
    string Description,
    string MinimumRequirements,
    string Location,
    string Type,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string Status,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    Guid CompanyId,
    string CompanyName,
    string CompanyCity,
    string CompanyProvince,
    string CompanyWebsite);

/// <summary>
/// One row of the Part 8 statistics report: a single active listing with its
/// per-status application breakdown and its rank by total applications.
/// Counts are <c>long</c> because PostgreSQL COUNT() returns bigint.
/// </summary>
public record JobListingStatsResponse(
    Guid JobListingId,
    string Title,
    long TotalApplications,
    long Submitted,
    long UnderReview,
    long Shortlisted,
    long Rejected,
    long Offered,
    long Rank);

public record CreateJobListingRequest(
    string Title,
    string Description,
    string MinimumRequirements,
    string Location,
    JobType Type,
    decimal? SalaryMin,
    decimal? SalaryMax,
    DateTime ExpiresAt);
// NOTE: CompanyId is intentionally NOT a client input. The owning company is
// taken from the authenticated employer's token claim, so an employer can only
// ever post for their own company — a forged id in the body has nowhere to land.

/// <summary>
/// An employer as seen by a client: the profile fields plus a count of the
/// company's currently-active, unexpired listings. The frontend uses this to
/// populate company pickers (create form, company-listings view, stats view)
/// without exposing GUIDs to the user. A flat projection, like the read-side
/// job queries — one SELECT, no navigation graph serialised.
/// </summary>
public record CompanyResponse(
    Guid Id,
    string Name,
    string City,
    string Province,
    string Industry,
    string Website,
    int? FoundedYear,
    long ActiveListingCount);

// ── PART 3: PAGINATION ───────────────────────────────────────────────────────

/// <summary>
/// HATEOAS-lite navigation links for a paged result, built from the current
/// request URL (extra improvement #1). <c>Next</c>/<c>Previous</c> are null at the
/// ends of the range so a client can branch on their presence.
/// </summary>
public record PageLinks(string Self, string? Next, string? Previous, string First, string Last);

/// <summary>
/// The standard envelope wrapping every paginated list response. <c>Data</c> holds
/// the page of items; the rest is the metadata a client needs to render paging
/// controls. <c>TotalPages</c>, <c>HasNextPage</c> and <c>HasPreviousPage</c> are
/// derived (see <see cref="Create"/>) so a caller can never construct an
/// inconsistent envelope by hand.
/// </summary>
public record PagedResponse<T>(
    IReadOnlyList<T> Data,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    bool HasNextPage,
    bool HasPreviousPage,
    PageLinks? Links = null)
{
    /// <summary>Build an envelope, deriving TotalPages / HasNext / HasPrevious from the inputs.</summary>
    public static PagedResponse<T> Create(IReadOnlyList<T> data, int page, int pageSize, int totalCount)
    {
        var totalPages = pageSize <= 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);
        return new PagedResponse<T>(
            data, page, pageSize, totalCount, totalPages,
            HasNextPage: page < totalPages,
            HasPreviousPage: page > 1);
    }
}

// ── PART 4: FILTERING + SORTING ──────────────────────────────────────────────

/// <summary>
/// The bound query string for the paginated job board: composable filters, a
/// sort key + direction, and the page window. Every filter is optional and is
/// only applied when non-null (AND semantics). Mutable <c>get; set;</c>
/// properties so ASP.NET complex-type model binding can populate it from
/// <c>[FromQuery]</c>. The assignment's <c>EmploymentType</c> maps to the 2.4
/// <see cref="JobType"/> and the default sort key <c>postedAt</c> maps to
/// <c>JobListing.CreatedAt</c>.
/// </summary>
public record JobListingFilterQuery
{
    // ── Filters (assignment) ──
    public string? Location { get; set; }
    public JobType? EmploymentType { get; set; }
    public decimal? SalaryMin { get; set; }
    public decimal? SalaryMax { get; set; }
    public Guid? CompanyId { get; set; }

    // ── Sorting (assignment + extras) ──
    public string? Sort { get; set; } = "postedAt";
    public string? Dir { get; set; } = "desc";

    // ── Paging ──
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;

    // ── Extras beyond the assignment ──
    /// <summary>Free-text search; uses the 2.4 GIN-indexed full-text column when present.</summary>
    public string? Q { get; set; }
    /// <summary>Only listings posted (CreatedAt) on or after this instant.</summary>
    public DateTime? PostedSince { get; set; }
    /// <summary>When true, only listings whose Location contains "remote" (case-insensitive).</summary>
    public bool? RemoteOnly { get; set; }
}

// ── PART 5: PATCH ────────────────────────────────────────────────────────────

/// <summary>
/// Partial update of a listing. EVERY field is nullable: a null field means
/// "leave unchanged", a non-null field means "set to this value". The service
/// re-runs the salary-range check only if a salary field was supplied and the
/// expiry check only if <see cref="ExpiresAt"/> was supplied.
/// </summary>
public record UpdateJobListingRequest(
    string? Title,
    string? Description,
    string? Location,
    JobType? EmploymentType,
    decimal? SalaryMin,
    decimal? SalaryMax,
    DateTime? ExpiresAt);

/// <summary>Body for the application-status transition endpoint (Part 5B).</summary>
public record UpdateApplicationStatusRequest(ApplicationStatus Status);

// ── AUTH ─────────────────────────────────────────────────────────────────────

/// <summary>Register a job-seeker account.</summary>
public record RegisterApplicantRequest(string FullName, string Email, string Password);

/// <summary>Register a recruiter account bound to an existing company.</summary>
public record RegisterEmployerRequest(string FullName, string Email, string Password, Guid CompanyId);

public record LoginRequest(string Email, string Password);

/// <summary>
/// What every successful register/login returns. The token carries the identity
/// and role claims the server trusts; the other fields are convenience copies for
/// the client's UI (it must NOT make security decisions from them). CompanyId is
/// present only for employers.
/// </summary>
public record AuthResponse(
    string Token,
    Guid UserId,
    string Email,
    string Role,
    Guid? CompanyId);

/// <summary>
/// One row of an applicant's own application history ("track applications").
/// Flat projection joining the application to its listing + company. <c>Status</c>
/// is the raw internal status (Submitted/UnderReview/…); <c>Stage</c> is the
/// applicant-friendly bucket (Applied/Pending/Accepted/Rejected) the seeker
/// actually cares about — see <see cref="Infrastructure.ApplicationStageMapper"/>.
/// </summary>
public record MyApplicationResponse(
    Guid JobListingId,
    string JobTitle,
    string CompanyName,
    string Status,
    string Stage,
    DateTime SubmittedAt);

/// <summary>
/// The status of one specific application the signed-in applicant submitted, used
/// by <c>GET /applications/me/{jobListingId}</c> so a seeker can track a single
/// job. 404 if they never applied to that listing.
/// </summary>
public record MyApplicationStatusResponse(
    Guid JobListingId,
    string JobTitle,
    string CompanyName,
    string Status,
    string Stage,
    DateTime SubmittedAt);

/// <summary>
/// A one-glance summary of the signed-in applicant's pipeline: a count per
/// friendly stage plus the total. Backs <c>GET /applications/me/summary</c>, which
/// a dashboard renders as "2 applied · 1 pending · 1 accepted · 0 rejected".
/// </summary>
public record MyApplicationsSummaryResponse(
    int Total,
    int Applied,
    int Pending,
    int Accepted,
    int Rejected);

// ── EMPLOYER: SEARCH APPLICANTS WHO APPLIED ──────────────────────────────────

/// <summary>
/// The bound query string for the employer's applicant search
/// (<c>GET /applicants/search</c>). Every filter is optional and composes with AND
/// semantics. The search is always scoped server-side to the applicants who have
/// applied to the caller's OWN company, so an employer can never browse another
/// company's candidate pool.
/// </summary>
public record ApplicantSearchQuery
{
    /// <summary>Substring match (ILIKE) over the applicant's Qualifications and Headline.</summary>
    public string? Qualification { get; set; }
    /// <summary>Only applicants with at least this many years of experience.</summary>
    public int? MinExperience { get; set; }
    /// <summary>Substring match (ILIKE) on the applicant's city.</summary>
    public string? City { get; set; }
    /// <summary>Restrict to applicants who applied to this specific listing of yours.</summary>
    public Guid? JobListingId { get; set; }

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

/// <summary>
/// One matching candidate in the employer's applicant search: their profile plus
/// how they relate to the employer's roles (how many of the company's listings
/// they applied to, and the stage of their most recent such application). A flat
/// projection — no entity graphs serialised.
/// </summary>
public record ApplicantSearchResponse(
    Guid ApplicantId,
    string FullName,
    string Email,
    string City,
    string Headline,
    int YearsOfExperience,
    string Qualifications,
    int ApplicationsToYourCompany,
    string LatestStage,
    DateTime LatestAppliedAt);

/// <summary>Body for applying to a listing — just an optional cover note.</summary>
public record ApplyRequest(string? CoverNote);

/// <summary>
/// A single application as returned by the Part 7 single-resource GET. Identity is
/// the composite key (JobListingId, ApplicantId) — there is no surrogate id.
/// </summary>
public record ApplicationResponse(
    Guid JobListingId,
    Guid ApplicantId,
    string Status,
    DateTime SubmittedAt,
    string CoverNote);
