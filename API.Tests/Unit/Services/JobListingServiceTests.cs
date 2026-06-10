using API.Tests.TestData;
using CareerHub.Api.DTOs;
using CareerHub.Api.Exceptions;
using CareerHub.Api.Models;
using CareerHub.Api.Repositories;
using CareerHub.Api.Services;
using FluentAssertions;
using NSubstitute;

namespace API.Tests.Unit.Services;

// ── UNIT TESTS: JobService business rules ────────────────────────────────────
// Pure unit tests: the repository is an NSubstitute double, so nothing touches a
// database and each test runs in well under 100ms. A FRESH substitute is created
// in the constructor for every test (xUnit news up the class per test), so no
// mock state ever leaks between tests.
//
// NOTE ON EXCEPTION TYPES: the brief's names say "InvalidSalaryException" /
// "InvalidListingException", but the real JobService throws ArgumentException for
// both salary and expiry violations (there is no such custom type in the code —
// see Exceptions/DomainExceptions.cs). Per the brief's instruction to "use
// whatever already exists; do not invent new ones", the test NAMES are kept as
// specified while the assertions target the actual ArgumentException.
public class JobListingServiceTests
{
    private readonly IJobListingRepository _repo = Substitute.For<IJobListingRepository>();
    private readonly JobService _sut;

    public JobListingServiceTests() => _sut = new JobService(_repo);

    private static CreateJobListingRequest ValidCreate(
        decimal? salaryMin = 50_000m, decimal? salaryMax = 90_000m, DateTime? expiresAt = null) =>
        new(
            Title: "Software Engineering Position",
            Description: "Build backend services.",
            MinimumRequirements: "Matric; 3+ years C#.",
            Location: "Sandton, Gauteng",
            Type: JobType.FullTime,
            SalaryMin: salaryMin,
            SalaryMax: salaryMax,
            ExpiresAt: expiresAt ?? DateTime.UtcNow.AddDays(30));

    [Fact]
    public async Task CreateAsync_WhenSalaryMaxLessThanSalaryMin_ThrowsInvalidSalaryException()
    {
        var request = ValidCreate(salaryMin: 50_000m, salaryMax: 40_000m);

        // Assert.ThrowsAsync reads more clearly than a FluentAssertions wrapper here.
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.CreateAsync(request, companyId: Guid.NewGuid()));
        ex.Message.Should().Contain("SalaryMax must be greater than SalaryMin");

        // The whole point: a rejected request must NOT reach the write path.
        await _repo.DidNotReceive().AddAsync(Arg.Any<JobListing>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_WhenExpiresAtIsInThePast_ThrowsInvalidListingException()
    {
        // Salaries omitted so the salary guard passes and the expiry guard is what trips.
        var request = ValidCreate(salaryMin: null, salaryMax: null, expiresAt: DateTime.UtcNow.AddDays(-1));

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.CreateAsync(request, companyId: Guid.NewGuid()));
        ex.Message.Should().Contain("ExpiresAt must be in the future");

        await _repo.DidNotReceive().AddAsync(Arg.Any<JobListing>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_WhenValid_CallsAddAsyncExactlyOnce()
    {
        var companyId = Guid.NewGuid();
        var request = ValidCreate();

        var id = await _sut.CreateAsync(request, companyId);

        id.Should().NotBeEmpty();
        // Exactly one write to the repository, then exactly one commit.
        await _repo.Received(1).AddAsync(
            Arg.Is<JobListing>(j => j.CompanyId == companyId && j.Title == request.Title),
            Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchAsync_WhenOnlySalaryMinChanged_CallsValidation()
    {
        var id = Guid.NewGuid();
        // The patch touches a salary field, so the salary guard MUST run. We make
        // the resulting on-disk range invalid (max < min) so that, if the guard
        // runs, it throws — proving the guard fired.
        var patched = new JobListingBuilder().WithId(id).WithSalary(100_000m, 50_000m).Build();
        _repo.PatchAsync(id, Arg.Any<UpdateJobListingRequest>(), Arg.Any<CancellationToken>())
             .Returns(patched);

        var req = new UpdateJobListingRequest(
            Title: null, Description: null, Location: null, EmploymentType: null,
            SalaryMin: 100_000m, SalaryMax: null, ExpiresAt: null);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _sut.PatchAsync(id, req));
        ex.Message.Should().Contain("SalaryMax must be greater than SalaryMin");

        // Validation failed → no commit.
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchAsync_WhenOnlyTitleChanged_DoesNotCallSalaryValidation()
    {
        var id = Guid.NewGuid();
        // The entity on disk has a DELIBERATELY invalid salary range. A title-only
        // patch must NOT re-validate it — if the conditional salary guard were ever
        // removed, this pre-existing bad range would be re-checked and the test
        // would start throwing, catching the regression.
        var patched = new JobListingBuilder().WithId(id).WithSalary(100_000m, 50_000m).Build();
        _repo.PatchAsync(id, Arg.Any<UpdateJobListingRequest>(), Arg.Any<CancellationToken>())
             .Returns(patched);

        var req = new UpdateJobListingRequest(
            Title: "Senior Software Engineering Position", Description: null, Location: null,
            EmploymentType: null, SalaryMin: null, SalaryMax: null, ExpiresAt: null);

        // No exception, and the write path IS reached (the equivalent of UpdateAsync
        // here is SaveChangesAsync — the repository has no separate UpdateAsync).
        var act = async () => await _sut.PatchAsync(id, req);
        await act.Should().NotThrowAsync();
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchAsync_WhenListingNotFound_ThrowsNotFoundException()
    {
        var id = Guid.NewGuid();
        _repo.PatchAsync(id, Arg.Any<UpdateJobListingRequest>(), Arg.Any<CancellationToken>())
             .Returns((JobListing?)null);

        var req = new UpdateJobListingRequest(
            Title: "Anything", Description: null, Location: null, EmploymentType: null,
            SalaryMin: null, SalaryMax: null, ExpiresAt: null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.PatchAsync(id, req));
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
