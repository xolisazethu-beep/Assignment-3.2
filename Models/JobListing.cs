using NpgsqlTypes;

namespace CareerHub.Api.Models;

public enum JobType { FullTime, PartTime, Contract, Internship, Learnership }

/// <summary>
/// Lifecycle of a listing. Stored as text in the database (see DbContext) so the
/// check constraints and seed scripts can read 'Active' / 'Closed' / 'Draft'
/// rather than opaque integers.
/// </summary>
public enum ListingStatus { Draft, Active, Closed }

/// <summary>
/// A job advert. Replaces the 2.3 <c>IsActive</c>/<c>PostedAt</c> pair with an
/// explicit <see cref="Status"/> lifecycle plus <see cref="CreatedAt"/> and
/// <see cref="ExpiresAt"/>, which the Assignment 2.4 query layer filters on.
/// </summary>
public class JobListing
{
    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The non-negotiable qualifications/skills a candidate must meet (e.g.
    /// "Matric; 3+ years C#; valid SA work permit"). Kept separate from
    /// <see cref="Description"/> so the detail view and search can treat the
    /// "must haves" distinctly from the marketing copy.
    /// </summary>
    public string MinimumRequirements { get; set; } = string.Empty;

    public string Location { get; set; } = string.Empty;   // e.g. "Sandton, Gauteng"
    public JobType Type { get; set; }

    // Salaries are in South African Rand (ZAR), monthly gross. Nullable because
    // many SA adverts say "market related" and omit the figure.
    public decimal? SalaryMin { get; set; }
    public decimal? SalaryMax { get; set; }

    public ListingStatus Status { get; set; } = ListingStatus.Active;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }

    // Foreign key + navigation to the owning employer.
    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    /// <summary>
    /// Full-text search vector over Title + Description. This is a STORED GENERATED
    /// column — PostgreSQL maintains it from to_tsvector('english', ...) on every
    /// insert/update. The application never writes it (see DbContext config).
    /// </summary>
    public NpgsqlTsVector SearchVector { get; set; } = null!;

    public ICollection<Application> Applications { get; set; } = [];
}
