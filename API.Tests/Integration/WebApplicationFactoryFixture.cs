using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace API.Tests.Integration;

// ── INTEGRATION FIXTURE: boots the REAL API in-process ───────────────────────
// Extends WebApplicationFactory<Program> (Program is made addressable by the
// `public partial class Program;` line at the bottom of Program.cs). The whole
// pipeline runs for real: routing, model binding, auth, EF Core, the exception
// handler — only the network socket is replaced by an in-memory transport.
//
// The environment is set to "Testing" so the host loads appsettings.Testing.json
// (its connection string + JWT key). ConfigureTestServices is intentionally left
// EMPTY: the brief requires the tests to run against the REAL configured database,
// not an in-memory or swapped-out one, so no service is replaced here.
public sealed class WebApplicationFactoryFixture : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Left empty on purpose — see the class comment. The real DbContext,
        // repositories and services configured in Program.cs are used as-is.
        builder.ConfigureTestServices(_ => { });
    }

    /// <summary>
    /// Case-insensitive options for deserialising API responses. The API emits
    /// camelCase JSON; using PropertyNameCaseInsensitive removes the single most
    /// common cause of integration tests passing locally but failing in CI.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
}
