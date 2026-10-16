using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using UrlShortener.Api.Data;
using UrlShortener.Api.Orchestration;

namespace UrlShortener.Tests;

public sealed class ApiIntegrationTests : IClassFixture<TestApiFactory>
{
    private const string ApprovalToken = "integration-test-review-token";
    private readonly TestApiFactory _factory;

    public ApiIntegrationTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task ShortUrlCreationRedirectAndAnalyticsWorkEndToEnd()
    {
        using var client = CreateClient(_factory);
        var code = $"test-{Guid.NewGuid():N}"[..32];

        var invalid = await client.PostAsJsonAsync("/api/urls", new { url = "javascript:alert(1)" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var create = await client.PostAsJsonAsync("/api/urls", new
        {
            url = "https://example.com/a/path?source=test",
            customCode = code
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var response = await create.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, response.GetProperty("code").GetString());
        Assert.StartsWith("https://short.example.test/r/", response.GetProperty("shortUrl").GetString());

        var duplicate = await client.PostAsJsonAsync("/api/urls", new
        {
            url = "https://example.org/",
            customCode = code
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var redirect = await client.GetAsync($"/r/{code}");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("https://example.com/a/path?source=test", redirect.Headers.Location?.ToString());

        var analytics = await client.GetFromJsonAsync<JsonElement>($"/api/urls/{code}/analytics");
        Assert.Equal(1, analytics.GetProperty("totalClicks").GetInt32());
        Assert.Single(analytics.GetProperty("clicksByDay").EnumerateArray());
    }

    [Fact]
    public async Task ExpiredShortUrlReturnsGoneWithoutRecordingAClick()
    {
        using var client = CreateClient(_factory);
        var code = $"exp-{Guid.NewGuid():N}"[..32];
        var create = await client.PostAsJsonAsync("/api/urls", new
        {
            url = "https://example.com/expired",
            customCode = code,
            expiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<UrlDbContext>>();
            await using var database = await factory.CreateDbContextAsync();
            var shortUrl = await database.ShortUrls.SingleAsync(url => url.Code == code);
            shortUrl.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await database.SaveChangesAsync();
        }

        var redirect = await client.GetAsync($"/r/{code}");
        Assert.Equal(HttpStatusCode.Gone, redirect.StatusCode);
        var analytics = await client.GetFromJsonAsync<JsonElement>($"/api/urls/{code}/analytics");
        Assert.Equal(0, analytics.GetProperty("totalClicks").GetInt32());
    }

    [Fact]
    public async Task OrchestrationPausesForApprovalsAndSynchronizesParallelStages()
    {
        using var client = CreateClient(_factory);
        var createResponse = await client.PostAsJsonAsync("/api/orchestration/runs", new { scenario = "greenfield" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var run = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var runId = run.GetProperty("id").GetGuid();
        Assert.Equal("NeedsApproval", run.GetProperty("state").GetString());
        Assert.Equal("AwaitingApproval", FindStage(run, "implementation").GetProperty("state").GetString());
        Assert.Equal("Completed", FindStage(run, "risk-review").GetProperty("state").GetString());

        var unauthenticated = await client.PostAsJsonAsync(
            $"/api/orchestration/runs/{runId}/approvals",
            new { stageId = "implementation", rationale = "Reviewed" });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        AddReviewerHeaders(client);
        var implementationApproval = await client.PostAsJsonAsync(
            $"/api/orchestration/runs/{runId}/approvals",
            new { stageId = "implementation", rationale = "Design and risk reviewed" });
        Assert.Equal(HttpStatusCode.OK, implementationApproval.StatusCode);
        run = await implementationApproval.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", FindStage(run, "tests").GetProperty("state").GetString());
        Assert.Equal("Completed", FindStage(run, "documentation").GetProperty("state").GetString());
        Assert.Equal("AwaitingApproval", FindStage(run, "release").GetProperty("state").GetString());

        var releaseApproval = await client.PostAsJsonAsync(
            $"/api/orchestration/runs/{runId}/approvals",
            new { stageId = "release", rationale = "Readiness evidence reviewed" });
        Assert.Equal(HttpStatusCode.OK, releaseApproval.StatusCode);
        run = await releaseApproval.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", run.GetProperty("state").GetString());
        Assert.Equal("reviewer.integration", run.GetProperty("decisions")[0].GetProperty("approver").GetString());
        Assert.Equal(2, run.GetProperty("decisions").GetArrayLength());

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<UrlDbContext>>();
            await using var database = await factory.CreateDbContextAsync();
            var auditEvents = await database.OrchestrationAuditEvents
                .AsNoTracking()
                .Where(auditEvent => auditEvent.RunId == runId.ToString("D"))
                .OrderBy(auditEvent => auditEvent.Sequence)
                .ToListAsync();
            var responseEventCount = run.GetProperty("events").GetArrayLength();
            Assert.Equal(responseEventCount, auditEvents.Count);
            Assert.Equal(Enumerable.Range(1, responseEventCount), auditEvents.Select(auditEvent => auditEvent.Sequence));
        }

        var metrics = await client.GetFromJsonAsync<JsonElement>("/api/orchestration/metrics");
        Assert.True(metrics.GetProperty("successRate").GetDouble() > 0);
        Assert.True(metrics.GetProperty("retryFrequency").GetDouble() >= 0);
        Assert.True(metrics.GetProperty("rollbackFrequency").GetDouble() >= 0);
        Assert.True(metrics.GetProperty("meanEndToEndLatencyMilliseconds").GetDouble() >= 0);
    }

    [Theory]
    [InlineData("greenfield", false)]
    [InlineData("brownfield", false)]
    [InlineData("ambiguous", true)]
    public async Task ScenarioRunsNormalizeRequirementsAndStopAtHumanGate(string scenario, bool highImpact)
    {
        using var client = CreateClient(_factory);
        var response = await client.PostAsJsonAsync("/api/orchestration/runs", new { scenario });

        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"Run creation returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var run = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(scenario, run.GetProperty("scenario").GetString());
        Assert.Equal(highImpact, run.GetProperty("highImpact").GetBoolean());
        Assert.Equal("NeedsApproval", run.GetProperty("state").GetString());
        Assert.Contains(run.GetProperty("events").EnumerateArray(), audit =>
            audit.GetProperty("type").GetString() == "ApprovalRequested");
    }

    [Fact]
    public async Task ReplanInvalidatesPreviousApprovalsAndIncrementsRevision()
    {
        using var client = CreateClient(_factory);
        var created = await client.PostAsJsonAsync("/api/orchestration/runs", new { scenario = "brownfield" });
        var run = await created.Content.ReadFromJsonAsync<JsonElement>();
        var runId = run.GetProperty("id").GetGuid();

        AddReviewerHeaders(client);
        var replanned = await client.PostAsJsonAsync(
            $"/api/orchestration/runs/{runId}/replan",
            new { requirements = "Add link expiry and daily click analytics with explicit retention." });

        Assert.True(replanned.IsSuccessStatusCode,
            $"Replan returned {(int)replanned.StatusCode}: {await replanned.Content.ReadAsStringAsync()}");
        run = await replanned.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, run.GetProperty("revision").GetInt32());
        Assert.Empty(run.GetProperty("decisions").EnumerateArray());
        Assert.Equal("NeedsApproval", run.GetProperty("state").GetString());
        Assert.Contains(run.GetProperty("events").EnumerateArray(), audit =>
            audit.GetProperty("type").GetString() == "Replanned" && audit.GetProperty("revision").GetInt32() == 2);
    }

    [Fact]
    public async Task ChangedUpstreamOutputReplansOnlyItsDescendants()
    {
        using var client = CreateClient(_factory);
        var created = await client.PostAsJsonAsync("/api/orchestration/runs", new { scenario = "greenfield" });
        var run = await created.Content.ReadFromJsonAsync<JsonElement>();
        var runId = run.GetProperty("id").GetGuid();
        AddReviewerHeaders(client);

        var replanned = await client.PostAsJsonAsync(
            $"/api/orchestration/runs/{runId}/replan",
            new
            {
                changedStageId = "architecture",
                changedOutput = "Design revised for active-active regional failover."
            });

        Assert.Equal(HttpStatusCode.OK, replanned.StatusCode);
        run = await replanned.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, run.GetProperty("revision").GetInt32());
        Assert.Equal("Completed", FindStage(run, "requirements").GetProperty("state").GetString());
        Assert.Equal("Design revised for active-active regional failover.",
            FindStage(run, "architecture").GetProperty("output").GetString());
        Assert.Contains("active-active regional failover",
            FindStage(run, "risk-review").GetProperty("output").GetString());
        Assert.Equal("AwaitingApproval", FindStage(run, "implementation").GetProperty("state").GetString());
        Assert.Contains(run.GetProperty("events").EnumerateArray(), audit =>
            audit.GetProperty("type").GetString() == "Replanned"
            && audit.GetProperty("stageId").GetString() == "architecture");
    }

    [Fact]
    public async Task FailedReleaseRetriesThenSafeStopsAndCanRollbackToApprovedCheckpoint()
    {
        await using var factory = new FailingReleaseApiFactory();
        using var client = CreateClient(factory);
        var created = await client.PostAsJsonAsync("/api/orchestration/runs", new { scenario = "greenfield" });
        var run = await created.Content.ReadFromJsonAsync<JsonElement>();
        var runId = run.GetProperty("id").GetGuid();
        AddReviewerHeaders(client);

        await client.PostAsJsonAsync($"/api/orchestration/runs/{runId}/approvals",
            new { stageId = "implementation", rationale = "Approved" });
        var failed = await client.PostAsJsonAsync($"/api/orchestration/runs/{runId}/approvals",
            new { stageId = "release", rationale = "Approved for test failure injection" });
        Assert.True(failed.IsSuccessStatusCode,
            $"Release approval returned {(int)failed.StatusCode}: {await failed.Content.ReadAsStringAsync()}");
        run = await failed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SafeStopped", run.GetProperty("state").GetString());
        Assert.Equal("Failed", FindStage(run, "release").GetProperty("state").GetString());
        Assert.Contains(run.GetProperty("events").EnumerateArray(), audit => audit.GetProperty("type").GetString() == "Retry");
        Assert.Contains(run.GetProperty("events").EnumerateArray(), audit => audit.GetProperty("type").GetString() == "Rollback");

        var rollback = await client.PostAsync($"/api/orchestration/runs/{runId}/rollback", content: null);
        Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);
        run = await rollback.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("RolledBack", run.GetProperty("state").GetString());
        Assert.Equal("Completed", FindStage(run, "implementation").GetProperty("state").GetString());
        Assert.Equal("Pending", FindStage(run, "release").GetProperty("state").GetString());

        var metrics = await client.GetFromJsonAsync<JsonElement>("/api/orchestration/metrics");
        Assert.True(metrics.GetProperty("meanTimeToRecoveryMilliseconds").GetDouble() >= 0);
    }

    [Fact]
    public async Task DocumentationStageUsesFallbackAfterBoundedRetries()
    {
        await using var factory = new FailingDocumentationApiFactory();
        using var client = CreateClient(factory);
        var created = await client.PostAsJsonAsync("/api/orchestration/runs", new { scenario = "greenfield" });
        var run = await created.Content.ReadFromJsonAsync<JsonElement>();
        var runId = run.GetProperty("id").GetGuid();
        AddReviewerHeaders(client);

        var implementationApproval = await client.PostAsJsonAsync($"/api/orchestration/runs/{runId}/approvals",
            new { stageId = "implementation", rationale = "Approved" });
        Assert.Equal(HttpStatusCode.OK, implementationApproval.StatusCode);
        run = await implementationApproval.Content.ReadFromJsonAsync<JsonElement>();
        var documentation = FindStage(run, "documentation");
        Assert.Equal("Completed", documentation.GetProperty("state").GetString());
        Assert.Equal(2, documentation.GetProperty("attempts").GetInt32());
        Assert.Contains("Fallback", documentation.GetProperty("output").GetString());
        Assert.Contains(run.GetProperty("events").EnumerateArray(), audit =>
            audit.GetProperty("type").GetString() == "FallbackApplied");
        Assert.Equal("AwaitingApproval", FindStage(run, "release").GetProperty("state").GetString());
    }

    private static JsonElement FindStage(JsonElement run, string stageId) =>
        run.GetProperty("stages").EnumerateArray().Single(stage =>
            stage.GetProperty("definition").GetProperty("id").GetString() == stageId);

    private static void AddReviewerHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Reviewer", "reviewer.integration");
        client.DefaultRequestHeaders.Add("X-Approval-Token", ApprovalToken);
    }

    private static HttpClient CreateClient(TestApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
}

public class TestApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"url-shortener-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Shortener"] = $"Data Source={_databasePath}",
                ["PublicBaseUrl"] = "https://short.example.test",
                ["ORCHESTRATION_APPROVAL_TOKEN"] = "integration-test-review-token"
            }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}

public sealed class FailingReleaseApiFactory : TestApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IStageExecutor>();
            services.AddSingleton<IStageExecutor, FailingReleaseStageExecutor>();
        });
    }
}

public sealed class FailingDocumentationApiFactory : TestApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IStageExecutor>();
            services.AddSingleton<IStageExecutor, FailingDocumentationStageExecutor>();
        });
    }
}

public sealed class FailingReleaseStageExecutor : IStageExecutor
{
    private readonly DeterministicStageExecutor _inner = new();

    public Task<StageExecution> ExecuteAsync(StageContext context, CancellationToken cancellationToken) =>
        context.StageId == "release"
            ? Task.FromException<StageExecution>(new InvalidOperationException("Injected release gate failure."))
            : _inner.ExecuteAsync(context, cancellationToken);
}

public sealed class FailingDocumentationStageExecutor : IStageExecutor
{
    private readonly DeterministicStageExecutor _inner = new();

    public Task<StageExecution> ExecuteAsync(StageContext context, CancellationToken cancellationToken) =>
        context.StageId == "documentation"
            ? Task.FromException<StageExecution>(new InvalidOperationException("Injected documentation failure."))
            : _inner.ExecuteAsync(context, cancellationToken);
}