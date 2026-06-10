using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CareerHub.Api.Tests;

/// <summary>
/// Boots the real API once (running migrations + the SA seed against the
/// Postgres in docker-compose) and shares it across the whole test collection.
/// REQUIRES a running database: `docker compose up -d` before `dotnet test`.
/// </summary>
public sealed class ApiFixture : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        // Use the Development config (local connection string + throwaway JWT key).
        builder.UseEnvironment("Development");
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;

/// <summary>Shared HTTP helpers for the integration tests.</summary>
public static class TestAuth
{
    /// <summary>Register a fresh applicant via the API and return a bearer-authenticated client.</summary>
    public static async Task<HttpClient> ApplicantClientAsync(ApiFixture factory)
    {
        var client = factory.CreateClient();
        var email = $"applicant-{Guid.NewGuid():N}@example.co.za";
        var resp = await client.PostAsJsonAsync("/api/v1/auth/register/applicant",
            new { FullName = "Test Applicant", Email = email, Password = "Password123!" });
        resp.EnsureSuccessStatusCode();
        var auth = await resp.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.GetProperty("token").GetString());
        return client;
    }

    /// <summary>Register a fresh employer (bound to the first seeded company) and return its client + companyId.</summary>
    public static async Task<(HttpClient Client, Guid CompanyId)> EmployerClientAsync(ApiFixture factory)
    {
        var client = factory.CreateClient();

        // Grab a real company id from the seed to bind the employer to.
        var companies = await client.GetFromJsonAsync<JsonElement>("/api/v1/companies");
        var companyId = companies[0].GetProperty("id").GetGuid();

        var email = $"employer-{Guid.NewGuid():N}@example.co.za";
        var resp = await client.PostAsJsonAsync("/api/v1/auth/register/employer",
            new { FullName = "Test Employer", Email = email, Password = "Password123!", CompanyId = companyId });
        resp.EnsureSuccessStatusCode();
        var auth = await resp.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.GetProperty("token").GetString());
        return (client, companyId);
    }
}
