using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CareerHub.Api.Tests;

[Collection("api")]
public class PatchAndEtagTests(ApiFixture factory)
{
    [Fact]
    public async Task Patch_partial_update_changes_only_supplied_fields()
    {
        var (client, _) = await TestAuth.EmployerClientAsync(factory);

        // Create a listing to patch.
        var create = await client.PostAsJsonAsync("/api/v1/jobs", new
        {
            Title = "PATCH Target Engineer",
            Description = "Original description for the patch test.",
            MinimumRequirements = "Matric; 2+ years experience.",
            Location = "Sandton, Gauteng",
            Type = "FullTime",
            SalaryMin = 50000m,
            SalaryMax = 70000m,
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        });
        create.EnsureSuccessStatusCode();
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // PATCH only the Location — Title/salary must be untouched.
        var patch = await client.PatchAsJsonAsync($"/api/v1/jobs/{id}", new { Location = "Cape Town, Western Cape" });
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        var dto = await client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{id}");
        Assert.Equal("Cape Town, Western Cape", dto.GetProperty("location").GetString());
        Assert.Equal("PATCH Target Engineer", dto.GetProperty("title").GetString());     // unchanged
        Assert.Equal(50000m, dto.GetProperty("salaryMin").GetDecimal());                 // unchanged
    }

    [Fact]
    public async Task Patch_with_inverted_salary_range_returns_400_problem_details()
    {
        var (client, _) = await TestAuth.EmployerClientAsync(factory);
        var create = await client.PostAsJsonAsync("/api/v1/jobs", new
        {
            Title = "Bad Salary Patch", Description = "desc desc desc", MinimumRequirements = "reqs",
            Location = "Durban, KwaZulu-Natal", Type = "Contract",
            SalaryMin = 40000m, SalaryMax = 60000m, ExpiresAt = DateTime.UtcNow.AddDays(20)
        });
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var patch = await client.PatchAsJsonAsync($"/api/v1/jobs/{id}", new { SalaryMin = 90000m, SalaryMax = 50000m });
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
        Assert.Contains("application/problem+json", patch.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task Etag_round_trip_returns_304_when_if_none_match_matches()
    {
        var client = factory.CreateClient();

        // Find any listing id from the board.
        var board = await client.GetFromJsonAsync<JsonElement>("/api/v1/jobs?pageSize=1");
        var data = board.GetProperty("data");
        Assert.True(data.GetArrayLength() > 0, "seed must contain at least one active listing");
        var id = data[0].GetProperty("id").GetGuid();

        var first = await client.GetAsync($"/api/v1/jobs/{id}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);

        var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/jobs/{id}");
        conditional.Headers.IfNoneMatch.Add(etag!);
        var second = await client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync()); // no body
    }
}
