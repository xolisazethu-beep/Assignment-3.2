using System.Net;

namespace CareerHub.Api.Tests;

[Collection("api")]
public class RateLimitAndCorsTests(ApiFixture factory)
{
    [Fact]
    public async Task Search_exceeding_30_per_minute_returns_429_with_retry_after()
    {
        var client = factory.CreateClient();

        // The "search" sliding window allows 30/60s. Fire well over the limit
        // CONCURRENTLY so the requests all land inside the same window — a
        // sequential awaited loop is slow enough in-process that the sliding
        // window frees segments as fast as we consume them and never trips.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 60).Select(_ => client.GetAsync("/api/v1/jobs/search?q=engineer")));

        var limited = responses.FirstOrDefault(r => r.StatusCode == HttpStatusCode.TooManyRequests);

        Assert.NotNull(limited);
        Assert.True(limited!.Headers.Contains("Retry-After"), "429 must carry Retry-After");
        var body = await limited.Content.ReadAsStringAsync();
        Assert.Contains("Rate limit exceeded. Please retry after", body);
    }

    [Fact]
    public async Task Cors_preflight_for_allowed_origin_echoes_allow_origin()
    {
        var client = factory.CreateClient();

        var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/jobs");
        preflight.Headers.Add("Origin", "http://localhost:3000");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");

        var resp = await client.SendAsync(preflight);

        Assert.True(resp.Headers.Contains("Access-Control-Allow-Origin"),
            "preflight must echo Access-Control-Allow-Origin for an allowed origin");
        Assert.Equal("http://localhost:3000",
            string.Join("", resp.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.True(resp.Headers.Contains("Access-Control-Allow-Credentials"),
            "credentialed policy must echo Access-Control-Allow-Credentials");
    }
}
