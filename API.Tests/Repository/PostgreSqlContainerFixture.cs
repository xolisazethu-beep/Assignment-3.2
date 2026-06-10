using CareerHub.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Respawn;
using Respawn.Graph;
using Testcontainers.PostgreSql;

namespace API.Tests.Repository;

// ── TESTCONTAINERS FIXTURE: a throwaway real Postgres ────────────────────────
// Spins up an actual postgres:16 container so the repository tests run the REAL
// migrations — check constraints, the STORED GENERATED tsvector column and the
// GIN index included — none of which the EF in-memory provider can model.
//
// Implements IAsyncLifetime, and is shared PER TEST CLASS via IClassFixture:
// container startup is ~2–3 seconds, far too expensive to pay per test. Between
// tests the data is wiped with Respawn (fast row deletes) rather than recreating
// the schema, so every test still sees a clean database.
public sealed class PostgreSqlContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .Build();

    private Respawner? _respawner;

    /// <summary>The connection string for the running throwaway container.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        // Apply the real schema ONCE up front so a Respawn checkpoint can be built
        // against it. Per-test CreateContext() also calls Migrate(), but that is a
        // no-op after this first run.
        var options = new DbContextOptionsBuilder<CareerHubDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        await using (var ctx = new CareerHubDbContext(options))
            await ctx.Database.MigrateAsync();

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        _respawner = await Respawner.CreateAsync(conn, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore = [new Table("__EFMigrationsHistory")],
        });
    }

    /// <summary>Delete all data (but keep the schema) so the next test starts clean.</summary>
    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await _respawner!.ResetAsync(conn);
    }

    public async Task DisposeAsync()
    {
        await _container.StopAsync();
        await _container.DisposeAsync();
    }
}
