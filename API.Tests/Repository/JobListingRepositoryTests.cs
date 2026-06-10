using API.Tests.TestData;
using CareerHub.Api.Data;
using CareerHub.Api.DTOs;
using CareerHub.Api.Models;
using CareerHub.Api.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace API.Tests.Repository;

// ── REPOSITORY TESTS: real Postgres via Testcontainers ───────────────────────
// These exercise the slice the unit tests cannot: actual SQL, the real migrations,
// PostgreSQL CHECK constraints, the compiled HasAppliedAsync query and the
// 'english' full-text stemmer. They are honestly slower than unit tests (a
// container round-trip per query), which is the price of testing behaviour that
// only exists inside Postgres.
//
// Cleanup: IAsyncLifetime.InitializeAsync resets the database with Respawn BEFORE
// every test, so each test owns a clean schema and the full suite passes when run
// twice in a row (test isolation — see README Part 1.3).
//
// NOTE ON THE BRIEF'S SIGNATURES: the brief writes calls like
// GetActiveListingsPagedAsync(page: 1, pageSize: 4) and HasAppliedAsync(listingId,
// applicantEmail). The real API takes a JobListingFilterQuery for paging and keys
// applications by applicant *Guid* (not email). The tests keep the brief's NAMES
// and intent while calling the actual method shapes.
public class JobListingRepositoryTests(PostgreSqlContainerFixture fixture)
    : IClassFixture<PostgreSqlContainerFixture>, IAsyncLifetime
{
    private readonly PostgreSqlContainerFixture _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private CareerHubDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CareerHubDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;
        var ctx = new CareerHubDbContext(options);
        ctx.Database.Migrate();   // applies real schema incl. check constraints + tsvector index (no-op after the first)
        return ctx;
    }

    // Seeds one company and returns its id (every listing needs a company FK).
    private static async Task<Guid> SeedCompanyAsync(CareerHubDbContext ctx)
    {
        var company = new CompanyBuilder().Build();
        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();
        return company.Id;
    }

    // Seeds one applicant and returns it (an application needs an applicant FK).
    private static async Task<Applicant> SeedApplicantAsync(CareerHubDbContext ctx, string? email = null)
    {
        var builder = new ApplicantBuilder();
        if (email is not null) builder.WithEmail(email);
        var applicant = builder.Build();
        ctx.Applicants.Add(applicant);
        await ctx.SaveChangesAsync();
        return applicant;
    }

    // Seeds N active, not-yet-expired listings for the given company.
    private static async Task SeedActiveListingsAsync(CareerHubDbContext ctx, Guid companyId, int count)
    {
        for (var i = 0; i < count; i++)
        {
            ctx.JobListings.Add(new JobListingBuilder()
                .WithCompanyId(companyId)
                .WithTitle($"Active Role {i}")
                .WithStatus(ListingStatus.Active)
                .WithCreatedAt(DateTime.UtcNow.AddDays(-i))      // distinct PostedAt values
                .WithExpiry(DateTime.UtcNow.AddDays(30))
                .Build());
        }
        await ctx.SaveChangesAsync();
    }

    // ── PART 5: PAGINATION ───────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveListingsPagedAsync_Page1_ReturnsCorrectCount()
    {
        await using var ctx = CreateContext();
        var companyId = await SeedCompanyAsync(ctx);
        await SeedActiveListingsAsync(ctx, companyId, 6);
        var repo = new JobListingRepository(ctx);

        var page = await repo.GetActiveListingsPagedAsync(
            new JobListingFilterQuery { Page = 1, PageSize = 4 });

        page.Data.Count().Should().Be(4);          // a full first page
        page.TotalCount.Should().Be(6);            // across all matching rows
        page.HasNextPage.Should().BeTrue();        // 6 rows / 4 per page → a page 2 exists
        page.HasPreviousPage.Should().BeFalse();   // page 1 has nothing before it
    }

    [Fact]
    public async Task GetActiveListingsPagedAsync_Page2_ReturnsDifferentRows()
    {
        await using var ctx = CreateContext();
        var companyId = await SeedCompanyAsync(ctx);
        await SeedActiveListingsAsync(ctx, companyId, 6);
        var repo = new JobListingRepository(ctx);

        var page1 = await repo.GetActiveListingsPagedAsync(new JobListingFilterQuery { Page = 1, PageSize = 3 });
        var page2 = await repo.GetActiveListingsPagedAsync(new JobListingFilterQuery { Page = 2, PageSize = 3 });

        var idsPage1 = page1.Data.Select(d => d.Id).ToHashSet();
        var idsPage2 = page2.Data.Select(d => d.Id).ToHashSet();

        // No row may appear on both pages — Skip/Take over a stable OrderBy must partition.
        idsPage1.Intersect(idsPage2).Should().BeEmpty();
    }

    [Fact]
    public async Task GetActiveListingsPagedAsync_ResultsAreOrderedByPostedAtDescending()
    {
        await using var ctx = CreateContext();
        var companyId = await SeedCompanyAsync(ctx);
        await SeedActiveListingsAsync(ctx, companyId, 5);   // CreatedAt spread across 5 days
        var repo = new JobListingRepository(ctx);

        var page = await repo.GetActiveListingsPagedAsync(new JobListingFilterQuery { Page = 1, PageSize = 5 });

        // Newest first: each listing's PostedAt (CreatedAt) must be >= the next one's.
        var postedAt = page.Data.Select(d => d.CreatedAt).ToList();
        postedAt.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task GetActiveListingsPagedAsync_ExcludesExpiredListings()
    {
        await using var ctx = CreateContext();
        var companyId = await SeedCompanyAsync(ctx);
        var repo = new JobListingRepository(ctx);

        // 3 active not-expired + 2 expired (ExpiresAt in the past).
        await SeedActiveListingsAsync(ctx, companyId, 3);
        ctx.JobListings.AddRange(
            new JobListingBuilder().WithCompanyId(companyId).WithTitle("Expired A")
                .WithStatus(ListingStatus.Active)
                .WithCreatedAt(DateTime.UtcNow.AddDays(-40)).WithExpiry(DateTime.UtcNow.AddDays(-1)).Build(),
            new JobListingBuilder().WithCompanyId(companyId).WithTitle("Expired B")
                .WithStatus(ListingStatus.Active)
                .WithCreatedAt(DateTime.UtcNow.AddDays(-50)).WithExpiry(DateTime.UtcNow.AddDays(-5)).Build());
        await ctx.SaveChangesAsync();

        var page = await repo.GetActiveListingsPagedAsync(new JobListingFilterQuery { Page = 1, PageSize = 20 });

        page.TotalCount.Should().Be(3);   // expired listings must not appear
        page.Data.Select(d => d.Title).Should().NotContain(t => t.StartsWith("Expired"));
    }

    [Fact]
    public async Task GetActiveListingsPagedAsync_ExcludesClosedAndExpiredListings()
    {
        await using var ctx = CreateContext();
        var companyId = await SeedCompanyAsync(ctx);
        var repo = new JobListingRepository(ctx);

        var active = new JobListingBuilder().WithCompanyId(companyId).WithTitle("Active Role")
            .WithStatus(ListingStatus.Active).WithExpiry(DateTime.UtcNow.AddDays(10)).Build();
        var closed = new JobListingBuilder().WithCompanyId(companyId).WithTitle("Closed Role")
            .WithStatus(ListingStatus.Closed).WithExpiry(DateTime.UtcNow.AddDays(10)).Build();
        var expired = new JobListingBuilder().WithCompanyId(companyId).WithTitle("Expired Role")
            .WithStatus(ListingStatus.Active)
            .WithCreatedAt(DateTime.UtcNow.AddDays(-40)).WithExpiry(DateTime.UtcNow.AddDays(-1)).Build();
        ctx.JobListings.AddRange(active, closed, expired);
        await ctx.SaveChangesAsync();

        var page = await repo.GetActiveListingsPagedAsync(new JobListingFilterQuery { Page = 1, PageSize = 50 });

        page.Data.Select(d => d.Title).Should().Contain("Active Role")
            .And.NotContain("Closed Role")
            .And.NotContain("Expired Role");
    }

    // ── PART 5: CHECK CONSTRAINTS (database-level, service bypassed) ──────────

    [Fact]
    public async Task CheckConstraint_RejectsSalaryMaxLessThanSalaryMin()
    {
        await using var ctx = CreateContext();
        var companyId = await SeedCompanyAsync(ctx);

        // An inverted salary range. The C# service guard is bypassed entirely here —
        // we add the entity straight to the context and save, so it is PostgreSQL's
        // ck_job_listings_salary_max_gt_min check constraint that must reject it.
        var bad = new JobListingBuilder().WithCompanyId(companyId).WithSalary(100_000m, 50_000m).Build();
        ctx.JobListings.Add(bad);

        var act = async () => await ctx.SaveChangesAsync();

        // Assert on the SQLSTATE class, not the message: 23xxx = integrity constraint
        // violation (23514 = check_violation). The message text is locale-dependent.
        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        ex.And.InnerException.Should().BeOfType<PostgresException>()
          .Which.SqlState.Should().StartWith("23");
    }

    [Fact]
    public async Task CheckConstraint_RejectsExpiresAtBeforeCreatedAt()
    {
        await using var ctx = CreateContext();
        var companyId = await SeedCompanyAsync(ctx);

        // ExpiresAt precedes CreatedAt. Salary is valid, so only the
        // ck_job_listings_expires_after_created constraint can reject this row.
        var now = DateTime.UtcNow;
        var bad = new JobListingBuilder().WithCompanyId(companyId)
            .WithCreatedAt(now).WithExpiry(now.AddDays(-1)).Build();
        ctx.JobListings.Add(bad);

        var act = async () => await ctx.SaveChangesAsync();

        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        ex.And.InnerException.Should().BeOfType<PostgresException>()
          .Which.SqlState.Should().StartWith("23");
    }

    // ── PART 6: COMPILED HasAppliedAsync QUERY ───────────────────────────────

    [Fact]
    public async Task HasAppliedAsync_WhenApplicationExists_ReturnsTrue()
    {
        await using var ctx = CreateContext();
        var companyId = await SeedCompanyAsync(ctx);
        var applicant = await SeedApplicantAsync(ctx, email: "thabo@example.co.za");

        var listing = new JobListingBuilder().WithCompanyId(companyId).WithTitle("Backend Engineer")
            .WithStatus(ListingStatus.Active).WithExpiry(DateTime.UtcNow.AddDays(30)).Build();
        ctx.JobListings.Add(listing);
        await ctx.SaveChangesAsync();

        ctx.Applications.Add(new ApplicationBuilder().ForListing(listing.Id).ByApplicant(applicant.Id).Build());
        await ctx.SaveChangesAsync();

        var applications = new ApplicationRepository(ctx);
        var result = await applications.HasAppliedAsync(applicant.Id, listing.Id);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasAppliedAsync_WhenNoApplicationExists_ReturnsFalse()
    {
        await using var ctx = CreateContext();
        var companyId = await SeedCompanyAsync(ctx);
        var applicant = await SeedApplicantAsync(ctx);

        var listing = new JobListingBuilder().WithCompanyId(companyId).WithTitle("Backend Engineer")
            .WithStatus(ListingStatus.Active).WithExpiry(DateTime.UtcNow.AddDays(30)).Build();
        ctx.JobListings.Add(listing);
        await ctx.SaveChangesAsync();
        // Deliberately no Application row seeded.

        var applications = new ApplicationRepository(ctx);
        var result = await applications.HasAppliedAsync(applicant.Id, listing.Id);

        result.Should().BeFalse();
    }

    // ── PART 5: FULL-TEXT SEARCH (english stemmer + GIN index) ───────────────

    [Fact]
    public async Task FullTextSearchAsync_ReturnsStemmedMatches()
    {
        await using var ctx = CreateContext();
        var companyId = await SeedCompanyAsync(ctx);
        var repo = new JobListingRepository(ctx);

        // The title contains "Engineering"; a search for "engineer" only matches via
        // the 'english' text-search config's stemmer (engineer/engineering → 'engin').
        // The in-memory provider has no concept of this, so it can ONLY be tested here.
        var listing = new JobListingBuilder().WithCompanyId(companyId)
            .WithTitle("Software Engineering Position")
            .WithStatus(ListingStatus.Active).WithExpiry(DateTime.UtcNow.AddDays(30)).Build();
        ctx.JobListings.Add(listing);
        await ctx.SaveChangesAsync(); // Postgres computes the stored tsvector on insert

        var results = await repo.SearchAsync("engineer");

        results.Should().ContainSingle(r => r.Title == "Software Engineering Position");
    }

    [Fact]
    public async Task FullTextSearchAsync_DoesNotReturnNonMatchingListings()
    {
        await using var ctx = CreateContext();
        var companyId = await SeedCompanyAsync(ctx);
        var repo = new JobListingRepository(ctx);

        // Exactly 2 of the 4 listings mention "Kubernetes"; the other 2 do not.
        ctx.JobListings.AddRange(
            new JobListingBuilder().WithCompanyId(companyId).WithTitle("Kubernetes Platform Engineer")
                .WithDescription("Operate Kubernetes clusters.").WithStatus(ListingStatus.Active)
                .WithExpiry(DateTime.UtcNow.AddDays(30)).Build(),
            new JobListingBuilder().WithCompanyId(companyId).WithTitle("Senior Kubernetes Administrator")
                .WithDescription("Manage Kubernetes at scale.").WithStatus(ListingStatus.Active)
                .WithExpiry(DateTime.UtcNow.AddDays(30)).Build(),
            new JobListingBuilder().WithCompanyId(companyId).WithTitle("Frontend React Developer")
                .WithDescription("Build SPA interfaces.").WithStatus(ListingStatus.Active)
                .WithExpiry(DateTime.UtcNow.AddDays(30)).Build(),
            new JobListingBuilder().WithCompanyId(companyId).WithTitle("Data Analyst")
                .WithDescription("Reporting and dashboards.").WithStatus(ListingStatus.Active)
                .WithExpiry(DateTime.UtcNow.AddDays(30)).Build());
        await ctx.SaveChangesAsync();

        var results = await repo.SearchAsync("kubernetes");

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.Title.Contains("Kubernetes"));
    }

    // ── EXTRAS (not in the brief, but cheap regression cover) ────────────────

    [Fact]
    public async Task AddAsync_ThenSaveChanges_PersistsListingRetrievableById()
    {
        await using var ctx = CreateContext();
        var companyId = await SeedCompanyAsync(ctx);
        var repo = new JobListingRepository(ctx);

        var listing = new JobListingBuilder().WithCompanyId(companyId).WithTitle("Backend Engineer").Build();
        await repo.AddAsync(listing);
        await repo.SaveChangesAsync();

        var detail = await repo.GetDetailByIdAsync(listing.Id);
        detail.Should().NotBeNull();
        detail!.Title.Should().Be("Backend Engineer");
    }

    [Fact]
    public async Task PatchAsync_AppliesOnlyNonNullFields_LeavingOthersIntact()
    {
        await using var ctx = CreateContext();
        var companyId = await SeedCompanyAsync(ctx);
        var repo = new JobListingRepository(ctx);

        var listing = new JobListingBuilder().WithCompanyId(companyId)
            .WithTitle("Original Title").WithLocation("Sandton, Gauteng").WithSalary(50_000m, 90_000m).Build();
        ctx.JobListings.Add(listing);
        await ctx.SaveChangesAsync();

        var req = new UpdateJobListingRequest(
            Title: null, Description: null, Location: "Cape Town, Western Cape",
            EmploymentType: null, SalaryMin: null, SalaryMax: null, ExpiresAt: null);
        await repo.PatchAsync(listing.Id, req);
        await repo.SaveChangesAsync();

        var detail = await repo.GetDetailByIdAsync(listing.Id);
        detail!.Location.Should().Be("Cape Town, Western Cape"); // changed
        detail.Title.Should().Be("Original Title");              // untouched
        detail.SalaryMin.Should().Be(50_000m);                   // untouched
    }
}
