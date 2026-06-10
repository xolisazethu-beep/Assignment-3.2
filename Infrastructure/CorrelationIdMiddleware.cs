namespace CareerHub.Api.Infrastructure;

/// <summary>
/// EXTRA #9: correlation-id enrichment. Reads an inbound <c>X-Correlation-Id</c>
/// header (or mints a new GUID when absent), echoes it back on the response, and
/// opens a logging scope carrying <c>CorrelationId</c> so every log line emitted
/// while handling the request is tagged with it — the basis for tracing one
/// request across structured logs.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var incoming)
                            && !string.IsNullOrWhiteSpace(incoming)
            ? incoming.ToString()
            : Guid.NewGuid().ToString();

        context.Items[HeaderName] = correlationId;

        // Echo it back as soon as the response starts (before the body is written).
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }
}
