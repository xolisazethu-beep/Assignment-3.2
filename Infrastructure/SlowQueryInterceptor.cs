using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CareerHub.Api.Infrastructure;

/// <summary>
/// PART 7. Logs any database command whose execution time exceeds a configurable
/// threshold. Holds no per-request state, so it is registered once as a Singleton.
/// EF Core hands us the measured <see cref="CommandExecutedEventData.Duration"/>,
/// so we do not need our own stopwatch.
/// </summary>
public sealed class SlowQueryInterceptor : DbCommandInterceptor
{
    private readonly ILogger<SlowQueryInterceptor> _logger;
    private readonly int _thresholdMs;

    public SlowQueryInterceptor(ILogger<SlowQueryInterceptor> logger, IConfiguration configuration)
    {
        _logger = logger;
        // Read once at construction. Defaults to 100ms when the setting is absent
        // so a missing config key never silently disables slow-query logging.
        _thresholdMs = configuration.GetValue<int?>("SlowQueryThresholdMs") ?? 100;
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        LogIfSlow(command, eventData);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        LogIfSlow(command, eventData);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    private void LogIfSlow(DbCommand command, CommandExecutedEventData eventData)
    {
        var elapsedMs = eventData.Duration.TotalMilliseconds;
        if (elapsedMs < _thresholdMs)
            return;

        _logger.LogWarning(
            "Slow query: {ElapsedMs:F1} ms (threshold {ThresholdMs} ms)\n{Sql}",
            elapsedMs, _thresholdMs, command.CommandText);
    }
}
