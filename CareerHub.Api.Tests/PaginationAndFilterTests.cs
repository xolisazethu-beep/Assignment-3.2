using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CareerHub.Api.Tests;

[Collection("api")]
public class PaginationAndFilterTests(ApiFixture factory)
{
    [Fact]
    public async Task Board_returns_paged_envelope_with_all_fields_and_total_count_header()
    {
        var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/jobs?page=1&pageSize=5");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(resp.Headers.Contains("X-Total-Count"), "X-Total-Count header must be present");

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // Envelope shape (Part 3).
        foreach (var field in new[] { "data", "page", "pageSize", "totalCount", "totalPages", "hasNextPage", "hasPreviousPage", "links" })
            Assert.True(body.TryGetProperty(field, out _), $"envelope is missing '{field}'");

        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(5, body.GetProperty("pageSize").GetInt32());
        Assert.True(body.GetProperty("data").GetArrayLength() <= 5, "page must not exceed pageSize");
        Assert.False(body.GetProperty("hasPreviousPage").GetBoolean(), "page 1 has no previous page");

        // HATEOAS links (extra #1).
        var links = body.GetProperty("links");
        Assert.False(string.IsNullOrEmpty(links.GetProperty("self").GetString()));
        Assert.False(string.IsNullOrEmpty(links.GetProperty("first").GetString()));
    }

    [Fact]
    public async Task PageSize_is_clamped_to_100()
    {
        var client = factory.CreateClient();
        var body = await client.GetFromJsonAsync<JsonElement>("/api/v1/jobs?pageSize=10000");
        Assert.Equal(100, body.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task Filters_compose_every_returned_row_matches_location()
    {
        var client = factory.CreateClient();
        // Cape Town is a seeded location; compose with a small page.
        var body = await client.GetFromJsonAsync<JsonElement>("/api/v1/jobs?location=Cape%20Town&pageSize=10&sort=salaryMax&dir=desc");

        foreach (var row in body.GetProperty("data").EnumerateArray())
            Assert.Contains("cape town", row.GetProperty("location").GetString()!.ToLowerInvariant());
    }
}
