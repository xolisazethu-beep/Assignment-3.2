using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CareerHub.Api.Tests;

/// <summary>
/// Covers the applicant "track my applications" endpoints and the employer
/// "search applicants by qualification" endpoint added on top of Assignment 3.1.
/// </summary>
[Collection("api")]
public class ApplicantFeaturesTests(ApiFixture factory)
{
    // ── APPLICANT: track application status (Applied/Pending/Accepted/Rejected) ──

    [Fact]
    public async Task New_applicant_starts_with_empty_history_and_zero_summary()
    {
        var client = await TestAuth.ApplicantClientAsync(factory);

        var history = await client.GetFromJsonAsync<JsonElement>("/api/v1/applications/me");
        Assert.Equal(0, history.GetArrayLength());

        var summary = await client.GetFromJsonAsync<JsonElement>("/api/v1/applications/me/summary");
        Assert.Equal(0, summary.GetProperty("total").GetInt32());
        Assert.Equal(0, summary.GetProperty("applied").GetInt32());
    }

    [Fact]
    public async Task Applying_shows_up_as_Applied_stage_in_history_summary_and_single_track()
    {
        var client = await TestAuth.ApplicantClientAsync(factory);

        // Grab an active listing from the board and apply to it.
        var board = await client.GetFromJsonAsync<JsonElement>("/api/v1/jobs?pageSize=1");
        var listingId = board.GetProperty("data")[0].GetProperty("id").GetGuid();

        var apply = await client.PostAsJsonAsync($"/api/v1/jobs/{listingId}/applications", new { CoverNote = "Keen!" });
        apply.EnsureSuccessStatusCode();

        // History row carries both raw Status and friendly Stage.
        var history = await client.GetFromJsonAsync<JsonElement>("/api/v1/applications/me");
        Assert.Equal(1, history.GetArrayLength());
        var row = history[0];
        Assert.Equal("Submitted", row.GetProperty("status").GetString());
        Assert.Equal("Applied", row.GetProperty("stage").GetString());

        // Stage filter: Applied returns it, Accepted does not.
        var applied = await client.GetFromJsonAsync<JsonElement>("/api/v1/applications/me?stage=Applied");
        Assert.Equal(1, applied.GetArrayLength());
        var accepted = await client.GetFromJsonAsync<JsonElement>("/api/v1/applications/me?stage=Accepted");
        Assert.Equal(0, accepted.GetArrayLength());

        // Summary reflects one Applied.
        var summary = await client.GetFromJsonAsync<JsonElement>("/api/v1/applications/me/summary");
        Assert.Equal(1, summary.GetProperty("total").GetInt32());
        Assert.Equal(1, summary.GetProperty("applied").GetInt32());

        // Single-application tracking by listing id.
        var single = await client.GetFromJsonAsync<JsonElement>($"/api/v1/applications/me/{listingId}");
        Assert.Equal("Applied", single.GetProperty("stage").GetString());
        Assert.Equal(listingId, single.GetProperty("jobListingId").GetGuid());
    }

    [Fact]
    public async Task Tracking_a_listing_never_applied_to_returns_404()
    {
        var client = await TestAuth.ApplicantClientAsync(factory);
        var resp = await client.GetAsync($"/api/v1/applications/me/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── EMPLOYER: search applicants who applied, by qualification/experience ──

    [Fact]
    public async Task Employer_search_is_scoped_and_filters_compose()
    {
        var (employer, _) = await TestAuth.EmployerClientAsync(factory);

        // Employer posts a brand-new listing so the applicant pool for it is known.
        var create = await employer.PostAsJsonAsync("/api/v1/jobs", new
        {
            Title = "Applicant Search Target", Description = "A role to attract one test applicant.",
            MinimumRequirements = "Matric.", Location = "Cape Town, Western Cape", Type = "FullTime",
            SalaryMin = 40000m, SalaryMax = 60000m, ExpiresAt = DateTime.UtcNow.AddDays(30)
        });
        create.EnsureSuccessStatusCode();
        var listingId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // A fresh applicant (0 years experience) applies to that listing.
        var applicant = await TestAuth.ApplicantClientAsync(factory);
        var apply = await applicant.PostAsJsonAsync($"/api/v1/jobs/{listingId}/applications", new { CoverNote = "Me!" });
        apply.EnsureSuccessStatusCode();

        // Search restricted to that listing → exactly the one applicant who applied.
        var scoped = await employer.GetFromJsonAsync<JsonElement>($"/api/v1/applicants/search?jobListingId={listingId}");
        Assert.Equal(1, scoped.GetProperty("totalCount").GetInt32());
        var hit = scoped.GetProperty("data")[0];
        Assert.True(hit.GetProperty("applicationsToYourCompany").GetInt32() >= 1);
        Assert.Equal("Applied", hit.GetProperty("latestStage").GetString());

        // Composing minExperience=1 excludes the 0-years applicant → empty page.
        var filtered = await employer.GetFromJsonAsync<JsonElement>(
            $"/api/v1/applicants/search?jobListingId={listingId}&minExperience=1");
        Assert.Equal(0, filtered.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Applicant_search_is_forbidden_for_applicants()
    {
        var applicant = await TestAuth.ApplicantClientAsync(factory);
        var resp = await applicant.GetAsync("/api/v1/applicants/search");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
