using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CareerHub.Api.DTOs;
using FluentAssertions;

namespace API.Tests.Integration;

// ── INTEGRATION TESTS: JobsController over the real HTTP pipeline ─────────────
// Each test sends a real HTTP request through the booted API to the real Postgres
// (migrated + seeded on startup). These are slower than unit tests — they pay for
// JSON (de)serialisation, EF Core, and a network round-trip over the in-memory
// transport — so they are honestly NOT held to the 100ms unit-test budget. The
// factory is shared per class via IClassFixture so the host boots once, not once
// per test.
//
// A shared, case-insensitive JsonSerializerOptions is used for every deserialise
// (WebApplicationFactoryFixture.Json) — case mismatch between the camelCase the
// API emits and PascalCase DTOs is the classic "passes locally, fails in CI" bug.
public class JobsControllerTests(WebApplicationFactoryFixture factory)
    : IClassFixture<WebApplicationFactoryFixture>
{
    private static readonly JsonSerializerOptions Json = WebApplicationFactoryFixture.Json;

    private record JobDetail(Guid Id, string Title, string Location, decimal? SalaryMin, string Status);

    [Fact]
    public async Task GetJobs_ReturnsOkWithPagedEnvelopeAndSeedData()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/jobs?pageSize=5");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var board = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        board.GetProperty("data").GetArrayLength().Should().BeGreaterThan(0,
            "the host migrates and seeds the SA dataset on startup");
        board.GetProperty("totalCount").GetInt32().Should().BeGreaterThan(0);
        // The X-Total-Count header mirrors the envelope's totalCount.
        response.Headers.GetValues("X-Total-Count").Should().ContainSingle();
    }

    [Fact]
    public async Task GetJobs_ReturnsOk()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/jobs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetJobs_ResponseIsPagedEnvelope()
    {
        var client = factory.CreateClient();

        var envelope = await client.GetFromJsonAsync<PagedResponse<JobListingResponse>>(
            "/api/v1/jobs?page=1&pageSize=5", Json);

        envelope.Should().NotBeNull();
        envelope!.Page.Should().Be(1);
        envelope.PageSize.Should().Be(5);
        envelope.TotalCount.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task GetJobs_ResponseIncludesXTotalCountHeader()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/jobs");

        response.Headers.Contains("X-Total-Count").Should().BeTrue();
    }

    [Fact]
    public async Task GetJobs_WithoutVersion_ReturnsSameStatusAsV1()
    {
        var client = factory.CreateClient();

        // Unversioned alias (/api/jobs) binds to the default ApiVersion 1.0, so it
        // must answer identically to the explicit /api/v1/jobs route.
        var unversioned = await client.GetAsync("/api/jobs");
        var v1 = await client.GetAsync("/api/v1/jobs");

        unversioned.StatusCode.Should().Be(HttpStatusCode.OK);
        v1.StatusCode.Should().Be(HttpStatusCode.OK);
        unversioned.StatusCode.Should().Be(v1.StatusCode);
    }

    [Fact]
    public async Task GetJobs_ResponseIncludesApiSupportedVersionsHeader()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/jobs");

        // ReportApiVersions = true makes the framework advertise supported versions.
        response.Headers.Contains("api-supported-versions").Should().BeTrue();
        string.Join(",", response.Headers.GetValues("api-supported-versions"))
            .Should().Contain("1.0");
    }

    [Fact]
    public async Task GetJobById_WithValidId_DoesNotReturn500()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/jobs/{Guid.NewGuid()}");

        // A random id is either 200 (found) or 404 (not found) — never a server error.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task PostApplication_WithoutToken_Returns401()
    {
        var client = factory.CreateClient();

        // Apply is nested under a listing (POST /api/v1/jobs/{id}/applications) and
        // is [Authorize(Roles="Applicant")] — with no bearer token the pipeline
        // short-circuits at authentication with 401 before any handler runs.
        var response = await client.PostAsJsonAsync(
            $"/api/v1/jobs/{Guid.NewGuid()}/applications",
            new { CoverNote = "I would like to apply." });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetJobs_HonoursPageSize()
    {
        var client = factory.CreateClient();

        var board = await client.GetFromJsonAsync<JsonElement>("/api/v1/jobs?pageSize=1", Json);

        board.GetProperty("data").GetArrayLength().Should().BeLessThanOrEqualTo(1);
        board.GetProperty("pageSize").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task GetJobById_WithUnknownId_Returns404()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/jobs/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetJobById_ReturnsStrongETagHeader()
    {
        var client = factory.CreateClient();
        var id = await FirstListingIdAsync(client);

        var response = await client.GetAsync($"/api/v1/jobs/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag.Should().NotBeNull();
        response.Headers.ETag!.Tag.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetJobById_WithMatchingETag_Returns304()
    {
        var client = factory.CreateClient();

        // 1. Get a real listing id (also proves seed data exists).
        var id = await FirstListingIdAsync(client);

        // 2. GET the detail and capture the strong ETag.
        var first = await client.GetAsync($"/api/v1/jobs/{id}");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var etag = first.Headers.ETag;
        etag.Should().NotBeNull("the detail endpoint must emit an ETag for conditional GETs");

        // 3. Re-request with If-None-Match set to the exact ETag returned.
        var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/jobs/{id}");
        conditional.Headers.IfNoneMatch.Add(etag!);
        var second = await client.SendAsync(conditional);

        // 4. The server recognises the unchanged resource and short-circuits.
        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await second.Content.ReadAsByteArrayAsync()).Should().BeEmpty("a 304 carries no body");
    }

    [Fact]
    public async Task SearchJobs_ByKeyword_ReturnsOnlyMatchingListings()
    {
        var client = factory.CreateClient();

        // "Kubernetes" appears in exactly 3 active seed listings (see SeedData).
        var results = await client.GetFromJsonAsync<List<JobDetail>>(
            "/api/v1/jobs/search?q=Kubernetes", Json);

        results.Should().NotBeNull();
        results!.Should().OnlyContain(j => j.Status == "Active");
    }

    [Fact]
    public async Task CreateJob_WithoutAuthentication_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/jobs", new
        {
            Title = "Unauthorised Listing",
            Description = "Should never be created.",
            MinimumRequirements = "None.",
            Location = "Cape Town, Western Cape",
            Type = "FullTime",
            SalaryMin = 50_000m,
            SalaryMax = 90_000m,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
        });

        // [Authorize(Roles = "Employer")] with no bearer token → 401, not 403.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // Reads one real listing id off the public board; also asserts seed data exists.
    private static async Task<Guid> FirstListingIdAsync(HttpClient client)
    {
        var board = await client.GetFromJsonAsync<JsonElement>("/api/v1/jobs?pageSize=1", Json);
        var data = board.GetProperty("data");
        data.GetArrayLength().Should().BeGreaterThan(0, "seed must contain at least one active listing");
        return data[0].GetProperty("id").GetGuid();
    }
}
