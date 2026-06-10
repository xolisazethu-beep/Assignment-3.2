using API.Tests.TestData;
using CareerHub.Api.Exceptions;
using CareerHub.Api.Models;
using CareerHub.Api.Repositories;
using CareerHub.Api.Services;
using FluentAssertions;
using NSubstitute;

namespace API.Tests.Unit.Services;

// ── UNIT TESTS: ApplicationService status-transition state machine ────────────
// The legal/illegal transition matrix lives in ApplicationService.LegalTransitions.
// These theory-driven tests pin every edge the brief specifies. Fresh substitutes
// per test; no database; each case runs in well under 100ms.
//
// NOTE: the illegal-transition test is named "...ReturnsBadRequest" per the brief,
// but the service layer THROWS an ArgumentException — it is the global
// ValidationExceptionHandler that turns that into an HTTP 400. At this unit level
// the correct, honest assertion is therefore on the thrown ArgumentException.
public class ApplicationServiceTests
{
    private readonly IApplicationRepository _applications = Substitute.For<IApplicationRepository>();
    private readonly IJobListingRepository _jobs = Substitute.For<IJobListingRepository>();
    private readonly ApplicationService _sut;

    public ApplicationServiceTests() => _sut = new ApplicationService(_applications, _jobs);

    private static readonly Guid JobId = Guid.NewGuid();
    private static readonly Guid ApplicantId = Guid.NewGuid();

    private void ArrangeExisting(ApplicationStatus current)
    {
        var app = new ApplicationBuilder().ForListing(JobId).ByApplicant(ApplicantId).WithStatus(current).Build();
        _applications.GetTrackedAsync(JobId, ApplicantId, Arg.Any<CancellationToken>()).Returns(app);
    }

    [Theory]
    [InlineData(ApplicationStatus.Submitted, ApplicationStatus.UnderReview)]
    [InlineData(ApplicationStatus.UnderReview, ApplicationStatus.Shortlisted)]
    [InlineData(ApplicationStatus.UnderReview, ApplicationStatus.Rejected)]
    [InlineData(ApplicationStatus.Shortlisted, ApplicationStatus.Offered)]
    [InlineData(ApplicationStatus.Shortlisted, ApplicationStatus.Rejected)]
    public async Task UpdateStatusAsync_WhenTransitionIsLegal_CallsUpdateAsync(
        ApplicationStatus from, ApplicationStatus to)
    {
        ArrangeExisting(from);

        await _sut.UpdateStatusAsync(JobId, ApplicantId, to);

        // The "update" is committed via SaveChangesAsync (no separate UpdateAsync on
        // the repo); the tracked entity must carry the new status.
        await _applications.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ApplicationStatus.Rejected, ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.Offered, ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.Rejected, ApplicationStatus.UnderReview)]
    [InlineData(ApplicationStatus.Offered, ApplicationStatus.Shortlisted)]
    public async Task UpdateStatusAsync_WhenTransitionIsIllegal_ReturnsBadRequest(
        ApplicationStatus from, ApplicationStatus to)
    {
        ArrangeExisting(from);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.UpdateStatusAsync(JobId, ApplicantId, to));
        ex.Message.Should().Contain("Illegal status transition");

        // An illegal transition must never be persisted.
        await _applications.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenApplicationNotFound_ThrowsNotFoundException()
    {
        _applications.GetTrackedAsync(JobId, ApplicantId, Arg.Any<CancellationToken>())
                     .Returns((Application?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _sut.UpdateStatusAsync(JobId, ApplicantId, ApplicationStatus.UnderReview));
        await _applications.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
