using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Asp.Versioning;
using CareerHub.Api.Data;
using CareerHub.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// All wiring lives in IServiceCollection extension methods (Part 7 requirement):
// the DbContext (with the slow-query interceptor), repositories and services.
builder.Services.AddCareerHubInfrastructure(builder.Configuration);
builder.Services.AddCareerHubRepositories();
builder.Services.AddCareerHubServices();

// Serialise/accept enums as their NAMES ("FullTime") rather than integers, so the
// JSON the API reads (CreateJobListingRequest.Type) matches what it writes
// (JobListingResponse already stringifies enums) and what the frontend sends.
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();

// ── PART 6: API VERSIONING ───────────────────────────────────────────────────
// Version travels in the URL segment (/api/v1/...). v1.0 is the default and is
// assumed when a client omits it, and every response advertises the versions it
// supports via the `api-supported-versions` header (exposed to browsers in the
// CORS policy above).
builder.Services
    .AddApiVersioning(options =>
    {
        options.DefaultApiVersion = new ApiVersion(1, 0);
        options.AssumeDefaultVersionWhenUnspecified = true;
        options.ReportApiVersions = true;
        options.ApiVersionReader = new UrlSegmentApiVersionReader();
    })
    .AddApiExplorer(options =>
    {
        // "'v'VVV" → v1, v2 … ; substitute the {version} route token in generated URLs.
        options.GroupNameFormat = "'v'VVV";
        options.SubstituteApiVersionInUrl = true;
    });

// ── JWT BEARER AUTHENTICATION ────────────────────────────────────────────────
// Tokens are signed/validated with the symmetric key from configuration (see
// appsettings "Jwt"; the key is a secret overridden out-of-band in real envs).
var jwt = builder.Configuration.GetSection("Jwt");
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!))
        };
    });
builder.Services.AddAuthorization();

// ── PART 2: CORS ─────────────────────────────────────────────────────────────
// A named policy the SPA frontend uses to call the API cross-origin. We list the
// explicit origins (local dev + a placeholder prod host) rather than
// AllowAnyOrigin() because AllowAnyOrigin() + AllowCredentials() is an illegal
// combination the CORS middleware THROWS on at startup (see README Part 2): the
// spec forbids the wildcard "*" from being echoed back together with
// Access-Control-Allow-Credentials: true, since that would let any site on the
// internet make credentialed requests. Listing origins lets us keep credentials.
const string CorsPolicy = "CareerHubFrontend";
builder.Services.AddCors(options =>
    options.AddPolicy(CorsPolicy, policy => policy
        .WithOrigins("http://localhost:3000", "https://careerhub.example.com")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()
        // Custom response headers a browser script can read off the response.
        // Without this list they are stripped from cross-origin reads.
        .WithExposedHeaders("X-Total-Count", "ETag", "api-supported-versions", "Retry-After")));

// Validation/constraint failures -> HTTP 400 Problem Details (extra #2/#3: a
// single exception handler translates every domain exception to RFC 7807, so
// controllers carry no try/catch).
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddProblemDetails();

// EXTRA #5: response compression (Brotli + Gzip) for JSON — large paginated
// payloads compress well. Enabled for HTTPS too (the payloads here aren't secrets).
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    o.MimeTypes = ["application/json", "application/problem+json"];
});

// EXTRA #7: in-memory IDistributedCache backing Idempotency-Key support on apply.
builder.Services.AddDistributedMemoryCache();

// EXTRA #8: health checks. /health/live is a liveness probe (process is up);
// /health/ready is a readiness probe that also pings PostgreSQL (tagged "ready").
builder.Services
    .AddHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")!,
        name: "postgres",
        tags: ["ready"]);

// ── PART 8: RATE LIMITING ────────────────────────────────────────────────────
builder.Services.AddRateLimiter(options =>
{
    // global — fixed window, 200 requests / 60s, ONE shared bucket for the whole
    // surface. This is the framework's GlobalLimiter, NOT a named policy: the
    // GlobalLimiter is always CHAINED with any endpoint-specific limiter, so the
    // stricter per-endpoint limits below (search/apply/post-listing) actually take
    // effect on top of it. (A named "global" policy applied via
    // MapControllers().RequireRateLimiting("global") does NOT chain — its metadata
    // is added after the action's [EnableRateLimiting] attribute and the middleware
    // resolves a single, last-wins policy, so it silently overrode the stricter
    // per-endpoint limits.)
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
        RateLimitPartition.GetFixedWindowLimiter("global", _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 200,
                Window = TimeSpan.FromSeconds(60),
                QueueLimit = 0
            }));

    // search — sliding window, 30 / 60s across 6 segments (10s each), no queue.
    // Sliding smooths the boundary burst a fixed window allows.
    options.AddSlidingWindowLimiter("search", o =>
    {
        o.PermitLimit = 30;
        o.Window = TimeSpan.FromSeconds(60);
        o.SegmentsPerWindow = 6;
        o.QueueLimit = 0;
    });

    // post-listing — fixed window, 10 per 60 MINUTES, no queue.
    options.AddFixedWindowLimiter("post-listing", o =>
    {
        o.PermitLimit = 10;
        o.Window = TimeSpan.FromMinutes(60);
        o.QueueLimit = 0;
    });

    // apply — fixed window, 5 per 60 MINUTES, no queue, PARTITIONED per user:
    // by the JWT `sub` claim when authenticated, falling back to the client IP.
    // So one applicant's 5/hour budget is theirs alone, not shared site-wide.
    options.AddPolicy("apply", httpContext =>
    {
        var partitionKey =
            httpContext.User.FindFirstValue("sub")
            ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(60),
                QueueLimit = 0
            });
    });

    // Rejected requests → 429 + Retry-After (seconds) + a plain-text body.
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        var retrySeconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? (int)Math.Ceiling(retryAfter.TotalSeconds)
            : 60;

        context.HttpContext.Response.Headers.RetryAfter = retrySeconds.ToString();
        context.HttpContext.Response.ContentType = "text/plain";
        await context.HttpContext.Response.WriteAsync(
            $"Rate limit exceeded. Please retry after {retrySeconds} seconds.", token);
    };
});

var app = builder.Build();

// EXTRA #9: tag every request (and its logs + response) with a correlation id,
// before anything else so even error responses carry it.
app.UseMiddleware<CorrelationIdMiddleware>();

// EXTRA #5: compress responses early in the pipeline.
app.UseResponseCompression();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();   // Scalar UI at /scalar/v1
}

// ── PART 2: CORS runs FIRST in the pipeline ──────────────────────────────────
// UseCors MUST sit before UseAuthentication/UseAuthorization so a rejected
// (401/403) cross-origin call still carries the Access-Control-* headers the
// browser needs — otherwise the browser surfaces an opaque CORS error and the
// SPA can never see the real status.
app.UseCors(CorsPolicy);

// ── PART 8: rate limiter sits right after CORS, before authentication ────────
app.UseRateLimiter();

// Authentication then authorization, in that order, for every request.
app.UseAuthentication();
app.UseAuthorization();

// Apply migrations and seed sample SA data on startup (dev convenience).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CareerHubDbContext>();
    await db.Database.MigrateAsync();
    await SeedData.SeedAsync(db);
    await SeedData.SeedDemoAccountsAsync(db); // login-ready demo accounts (idempotent)
}

// Every request is covered by the GlobalLimiter (200/60s); the search/apply/
// post-listing actions layer their own stricter [EnableRateLimiting] on top, which
// the framework CHAINS with the GlobalLimiter (see AddRateLimiter above).
app.MapControllers();

// EXTRA #8: liveness (no dependency checks) vs readiness (pings Postgres).
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false // run no checks — just confirm the process answers
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready") // include the Postgres check
});

app.Run();

// Exposed so integration tests / EF tooling can reference the entry assembly.
public partial class Program;
