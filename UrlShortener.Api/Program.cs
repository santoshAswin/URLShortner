using System.Threading.RateLimiting;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Data;
using UrlShortener.Api.Orchestration;
using UrlShortener.Api.Services;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Shortener")
    ?? "Data Source=urlshortener.db";

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddDbContextFactory<UrlDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddScoped<UrlShortenerService>();
builder.Services.AddSingleton<OrchestrationEngine>();
builder.Services.AddSingleton<IStageExecutor, DeterministicStageExecutor>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("url-writes", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var app = builder.Build();
app.UseRateLimiter();

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<UrlDbContext>();
    await database.Database.EnsureCreatedAsync();
    await database.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "OrchestrationAuditEvents" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_OrchestrationAuditEvents" PRIMARY KEY AUTOINCREMENT,
            "RunId" TEXT NOT NULL,
            "Sequence" INTEGER NOT NULL,
            "At" TEXT NOT NULL,
            "Type" TEXT NOT NULL,
            "StageId" TEXT NULL,
            "Actor" TEXT NOT NULL,
            "Details" TEXT NOT NULL,
            "Revision" INTEGER NOT NULL,
            CONSTRAINT "FK_OrchestrationAuditEvents_OrchestrationRuns_RunId"
                FOREIGN KEY ("RunId") REFERENCES "OrchestrationRuns" ("Id") ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_OrchestrationAuditEvents_RunId_Sequence"
            ON "OrchestrationAuditEvents" ("RunId", "Sequence");
        CREATE INDEX IF NOT EXISTS "IX_OrchestrationAuditEvents_RunId_At"
            ON "OrchestrationAuditEvents" ("RunId", "At");
        """);
}

app.MapGet("/health", async (UrlDbContext database, CancellationToken cancellationToken) =>
    await database.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ok" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapPost("/api/urls", async (
    CreateShortUrlRequest request,
    HttpContext httpContext,
    IConfiguration configuration,
    UrlShortenerService shortener,
    CancellationToken cancellationToken) =>
{
    if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var destination)
        || (destination.Scheme != Uri.UriSchemeHttp && destination.Scheme != Uri.UriSchemeHttps)
        || !string.IsNullOrEmpty(destination.UserInfo)
        || request.Url.Length > 2048)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["url"] = ["Use an absolute HTTP or HTTPS URL (maximum 2048 characters)."]
        });
    }

    if (request.ExpiresAt is not null && request.ExpiresAt <= DateTimeOffset.UtcNow)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["expiresAt"] = ["Expiry must be in the future."]
        });
    }

    try
    {
        var url = await shortener.CreateAsync(
            destination,
            request.CustomCode,
            request.ExpiresAt,
            cancellationToken);
        var publicBaseUrl = configuration["PublicBaseUrl"]?.TrimEnd('/');
        if (publicBaseUrl is not null
            && (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var configuredPublicUri)
                || (configuredPublicUri.Scheme != Uri.UriSchemeHttp && configuredPublicUri.Scheme != Uri.UriSchemeHttps)))
        {
            return Results.Problem("PublicBaseUrl must be an absolute HTTP or HTTPS origin.");
        }

        var origin = publicBaseUrl ?? $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
        var shortUrl = $"{origin}/r/{url.Code}";
        return Results.Created($"/api/urls/{url.Code}", new CreateShortUrlResponse(
            url.Code,
            shortUrl,
            url.DestinationUrl,
            url.CreatedAt,
            url.ExpiresAt));
    }
    catch (InvalidShortCodeException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["customCode"] = [exception.Message]
        });
    }
    catch (ShortCodeConflictException)
    {
        return Results.Conflict(new { error = "The requested short code is already in use." });
    }
})
.RequireRateLimiting("url-writes");

app.MapGet("/r/{code}", async (
    string code,
    UrlShortenerService shortener,
    CancellationToken cancellationToken) =>
{
    var url = await shortener.FindAsync(code, cancellationToken);
    if (url is null)
    {
        return Results.NotFound();
    }

    if (url.ExpiresAt is not null && url.ExpiresAt <= DateTimeOffset.UtcNow)
    {
        return Results.StatusCode(StatusCodes.Status410Gone);
    }

    await shortener.RecordClickAsync(url, cancellationToken);
    return Results.Redirect(url.DestinationUrl, permanent: false);
});

app.MapGet("/api/urls/{code}/analytics", async (
    string code,
    UrlShortenerService shortener,
    CancellationToken cancellationToken) =>
{
    var analytics = await shortener.GetAnalyticsAsync(code, cancellationToken);
    return analytics is null ? Results.NotFound() : Results.Ok(analytics);
});

app.MapPost("/api/orchestration/runs", async (
    CreateRunRequest request,
    OrchestrationEngine orchestration,
    CancellationToken cancellationToken) =>
{
    try
    {
        var run = await orchestration.CreateAsync(request, cancellationToken);
        return Results.Created($"/api/orchestration/runs/{run.Id}", run);
    }
    catch (OrchestrationInputException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["request"] = [exception.Message]
        });
    }
});

app.MapGet("/api/orchestration/runs/{runId:guid}", async (
    Guid runId,
    OrchestrationEngine orchestration,
    CancellationToken cancellationToken) =>
{
    var run = await orchestration.GetAsync(runId, cancellationToken);
    return run is null ? Results.NotFound() : Results.Ok(run);
});

app.MapPost("/api/orchestration/runs/{runId:guid}/approvals", async (
    Guid runId,
    ApproveStageRequest request,
    HttpContext httpContext,
    IConfiguration configuration,
    OrchestrationEngine orchestration,
    CancellationToken cancellationToken) =>
{
    var reviewer = httpContext.Request.Headers["X-Reviewer"].ToString();
    if (!IsHumanAuthorized(httpContext, configuration) || string.IsNullOrWhiteSpace(reviewer))
    {
        return Results.Unauthorized();
    }

    try
    {
        return Results.Ok(await orchestration.ApproveAsync(runId, request, reviewer, cancellationToken));
    }
    catch (OrchestrationRunNotFoundException)
    {
        return Results.NotFound();
    }
    catch (OrchestrationInputException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["approval"] = [exception.Message]
        });
    }
    catch (OrchestrationConflictException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
});

app.MapPost("/api/orchestration/runs/{runId:guid}/replan", async (
    Guid runId,
    ReplanRunRequest request,
    HttpContext httpContext,
    IConfiguration configuration,
    OrchestrationEngine orchestration,
    CancellationToken cancellationToken) =>
{
    var reviewer = httpContext.Request.Headers["X-Reviewer"].ToString();
    if (!IsHumanAuthorized(httpContext, configuration) || string.IsNullOrWhiteSpace(reviewer))
    {
        return Results.Unauthorized();
    }

    try
    {
        return Results.Ok(await orchestration.ReplanAsync(runId, request, reviewer, cancellationToken));
    }
    catch (OrchestrationRunNotFoundException)
    {
        return Results.NotFound();
    }
    catch (OrchestrationInputException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["requirements"] = [exception.Message]
        });
    }
});

app.MapPost("/api/orchestration/runs/{runId:guid}/rollback", async (
    Guid runId,
    HttpContext httpContext,
    IConfiguration configuration,
    OrchestrationEngine orchestration,
    CancellationToken cancellationToken) =>
{
    var reviewer = httpContext.Request.Headers["X-Reviewer"].ToString();
    if (!IsHumanAuthorized(httpContext, configuration) || string.IsNullOrWhiteSpace(reviewer))
    {
        return Results.Unauthorized();
    }

    try
    {
        return Results.Ok(await orchestration.RollbackAsync(runId, reviewer, cancellationToken));
    }
    catch (OrchestrationRunNotFoundException)
    {
        return Results.NotFound();
    }
    catch (OrchestrationConflictException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
});

app.MapGet("/api/orchestration/metrics", async (
    OrchestrationEngine orchestration,
    CancellationToken cancellationToken) =>
    Results.Ok(await orchestration.GetMetricsAsync(cancellationToken)));

app.Run();

static bool IsHumanAuthorized(HttpContext context, IConfiguration configuration)
{
    var configuredToken = configuration["ORCHESTRATION_APPROVAL_TOKEN"];
    var suppliedToken = context.Request.Headers["X-Approval-Token"].ToString();
    return !string.IsNullOrWhiteSpace(configuredToken)
        && !string.IsNullOrWhiteSpace(suppliedToken)
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(configuredToken),
            Encoding.UTF8.GetBytes(suppliedToken));
}

public partial class Program;

public sealed record CreateShortUrlRequest(
    string? Url,
    string? CustomCode = null,
    DateTimeOffset? ExpiresAt = null);

public sealed record CreateShortUrlResponse(
    string Code,
    string ShortUrl,
    string DestinationUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt);
