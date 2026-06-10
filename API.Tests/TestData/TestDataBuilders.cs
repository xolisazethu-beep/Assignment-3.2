using CareerHub.Api.Models;

namespace API.Tests.TestData;

// ── BUILDER PATTERN FOR TEST ENTITIES ────────────────────────────────────────
// Not in the brief, but added deliberately: 30+ tests each newing up a JobListing
// / Application / Company by hand is unmaintainable and drifts the moment a
// required property is added to a model. Each builder starts from a VALID default
// and exposes fluent overrides for only the field a given test cares about, so a
// test reads as "a valid listing, except the salary range is inverted".

/// <summary>Fluent builder for <see cref="Company"/> test entities.</summary>
public sealed class CompanyBuilder
{
    private Guid _id = Guid.NewGuid();
    private string _name = "Takealot";
    private string _city = "Cape Town";
    private string _province = "Western Cape";
    private string _industry = "E-commerce";
    private string _website = "https://www.takealot.com";
    private int? _foundedYear = 2011;

    public CompanyBuilder WithId(Guid id) { _id = id; return this; }
    public CompanyBuilder WithName(string name) { _name = name; return this; }
    public CompanyBuilder WithCity(string city) { _city = city; return this; }

    public Company Build() => new()
    {
        Id = _id,
        Name = _name,
        City = _city,
        Province = _province,
        Industry = _industry,
        Website = _website,
        FoundedYear = _foundedYear,
    };
}

/// <summary>Fluent builder for <see cref="JobListing"/> test entities.</summary>
public sealed class JobListingBuilder
{
    private Guid _id = Guid.NewGuid();
    private string _title = "Software Engineering Position";
    private string _description = "Build and maintain backend services in C# and PostgreSQL.";
    private string _minimumRequirements = "Matric; 3+ years C#; valid SA work permit.";
    private string _location = "Sandton, Gauteng";
    private JobType _type = JobType.FullTime;
    private decimal? _salaryMin = 50_000m;
    private decimal? _salaryMax = 90_000m;
    private ListingStatus _status = ListingStatus.Active;
    private DateTime _createdAt = DateTime.UtcNow.AddDays(-1);
    private DateTime _expiresAt = DateTime.UtcNow.AddDays(30);
    private Guid _companyId = Guid.NewGuid();
    private Company? _company;

    public JobListingBuilder WithId(Guid id) { _id = id; return this; }
    public JobListingBuilder WithTitle(string title) { _title = title; return this; }
    public JobListingBuilder WithDescription(string description) { _description = description; return this; }
    public JobListingBuilder WithLocation(string location) { _location = location; return this; }
    public JobListingBuilder WithType(JobType type) { _type = type; return this; }

    /// <summary>Set both salary bounds at once. Either may be null ("market related").</summary>
    public JobListingBuilder WithSalary(decimal? min, decimal? max) { _salaryMin = min; _salaryMax = max; return this; }

    public JobListingBuilder WithStatus(ListingStatus status) { _status = status; return this; }
    public JobListingBuilder WithCreatedAt(DateTime createdAt) { _createdAt = createdAt; return this; }
    public JobListingBuilder WithExpiry(DateTime expiresAt) { _expiresAt = expiresAt; return this; }

    /// <summary>Attach the listing to a company (sets both the FK and the navigation).</summary>
    public JobListingBuilder ForCompany(Company company) { _company = company; _companyId = company.Id; return this; }
    public JobListingBuilder WithCompanyId(Guid companyId) { _companyId = companyId; return this; }

    public JobListing Build()
    {
        var listing = new JobListing
        {
            Id = _id,
            Title = _title,
            Description = _description,
            MinimumRequirements = _minimumRequirements,
            Location = _location,
            Type = _type,
            SalaryMin = _salaryMin,
            SalaryMax = _salaryMax,
            Status = _status,
            CreatedAt = _createdAt,
            ExpiresAt = _expiresAt,
            CompanyId = _companyId,
        };
        if (_company is not null) listing.Company = _company;
        return listing;
    }
}

/// <summary>Fluent builder for <see cref="Applicant"/> test entities.</summary>
public sealed class ApplicantBuilder
{
    private Guid _id = Guid.NewGuid();
    private string _fullName = "Thabo Nkosi";
    private string _email = $"applicant-{Guid.NewGuid():N}@example.co.za";
    private string _passwordHash = "x";
    private string _city = "Johannesburg";
    private string _headline = "Backend Developer, 4 yrs";
    private int _yearsOfExperience = 4;
    private string _qualifications = "BSc Computer Science (UCT); C#, PostgreSQL.";

    public ApplicantBuilder WithId(Guid id) { _id = id; return this; }
    public ApplicantBuilder WithEmail(string email) { _email = email; return this; }
    public ApplicantBuilder WithCity(string city) { _city = city; return this; }
    public ApplicantBuilder WithExperience(int years) { _yearsOfExperience = years; return this; }

    public Applicant Build() => new()
    {
        Id = _id,
        FullName = _fullName,
        Email = _email,
        PasswordHash = _passwordHash,
        City = _city,
        Headline = _headline,
        YearsOfExperience = _yearsOfExperience,
        Qualifications = _qualifications,
    };
}

/// <summary>Fluent builder for <see cref="Application"/> test entities.</summary>
public sealed class ApplicationBuilder
{
    private Guid _jobListingId = Guid.NewGuid();
    private Guid _applicantId = Guid.NewGuid();
    private ApplicationStatus _status = ApplicationStatus.Submitted;
    private DateTime _submittedAt = DateTime.UtcNow.AddHours(-1);
    private string _coverNote = "I am keen to apply for this role.";

    public ApplicationBuilder ForListing(Guid jobListingId) { _jobListingId = jobListingId; return this; }
    public ApplicationBuilder ByApplicant(Guid applicantId) { _applicantId = applicantId; return this; }
    public ApplicationBuilder WithStatus(ApplicationStatus status) { _status = status; return this; }
    public ApplicationBuilder SubmittedAt(DateTime submittedAt) { _submittedAt = submittedAt; return this; }

    public Application Build() => new()
    {
        JobListingId = _jobListingId,
        ApplicantId = _applicantId,
        Status = _status,
        SubmittedAt = _submittedAt,
        CoverNote = _coverNote,
    };
}
