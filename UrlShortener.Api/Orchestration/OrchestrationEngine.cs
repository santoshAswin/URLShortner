using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Data;

namespace UrlShortener.Api.Orchestration;

public sealed class OrchestrationEngine(
    IDbContextFactory<UrlDbContext> databaseFactory,
    IStageExecutor executor)
{
    private const int MaximumAttempts = 2;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _runLocks = new();

    private static readonly StageDefinition[] StageGraph =
    [
        new("requirements", "Requirements", "Non-empty request", "Acceptance criteria recorded", []),
        new("architecture", "Architecture and design", "Requirements complete", "Components and data flow documented", ["requirements"]),
        new("risk-review", "Policy and risk review", "Design complete", "Security and change risks assessed", ["architecture"]),
        new("implementation", "Implementation artifact", "Design and risk review complete", "Reviewable change artifact produced", ["architecture", "risk-review"], true),
        new("tests", "Validation plan", "Implementation artifact produced", "Validation coverage and risks recorded", ["implementation"]),
        new("documentation", "Documentation", "Implementation artifact produced", "Setup and API documentation recorded", ["implementation"]),
        new("release", "Release readiness", "Tests and documentation complete", "Readiness evidence reviewed", ["tests", "documentation"], true)
    ];

    public async Task<OrchestrationRun> CreateAsync(
        CreateRunRequest request,
        CancellationToken cancellationToken)
    {
        var scenario = request.Scenario.Trim().ToLowerInvariant();
        if (scenario is not ("greenfield" or "brownfield" or "ambiguous"))
        {
            throw new OrchestrationInputException("Scenario must be greenfield, brownfield, or ambiguous.");
        }

        var requirements = string.IsNullOrWhiteSpace(request.Requirements)
            ? ScenarioRequirement(scenario)
            : request.Requirements.Trim();
        if (requirements.Length > 4000)
        {
            throw new OrchestrationInputException("Requirements must not exceed 4000 characters.");
        }

        var now = DateTimeOffset.UtcNow;
        var run = new OrchestrationRun
        {
            Id = Guid.NewGuid(),
            Scenario = scenario,
            Requirements = requirements,
            HighImpact = scenario == "ambiguous" || HasHighImpactTerms(requirements),
            CreatedAt = now,
            UpdatedAt = now,
            Stages = StageGraph.Select(definition => new StageSnapshot { Definition = definition }).ToList()
        };
        AddEvent(run, "RunCreated", null, "system", $"Scenario '{scenario}' accepted.");

        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        database.OrchestrationRuns.Add(new OrchestrationRunEntity
        {
            Id = run.Id.ToString("D"),
            Scenario = run.Scenario,
            SnapshotJson = JsonSerializer.Serialize(run, OrchestrationJson.Options),
            CreatedAt = now,
            UpdatedAt = now
        });
        await database.SaveChangesAsync(cancellationToken);
        await ExecuteUntilGateAsync(database, run, cancellationToken);
        await PersistAsync(database, run, cancellationToken);
        return run;
    }

    public async Task<OrchestrationRun?> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await LoadAsync(database, runId, cancellationToken);
    }

    public async Task<OrchestrationRun> ApproveAsync(
        Guid runId,
        ApproveStageRequest request,
        string reviewer,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reviewer) || string.IsNullOrWhiteSpace(request.Rationale))
        {
            throw new OrchestrationInputException("Reviewer and rationale are required.");
        }

        return await WithRunLockAsync(runId, async () =>
        {
            await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
            var run = await LoadRequiredAsync(database, runId, cancellationToken);
            var stage = run.Stages.SingleOrDefault(item => item.Definition.Id == request.StageId);
            if (stage is null || !stage.Definition.RequiresApproval
                || stage.State != StageState.AwaitingApproval || run.State != RunState.NeedsApproval)
            {
                throw new OrchestrationConflictException("This run is not awaiting approval for that stage.");
            }

            stage.Approved = true;
            stage.State = StageState.Pending;
            run.Decisions.Add(new DecisionRecord(
                stage.Definition.Id,
                reviewer.Trim(),
                request.Rationale.Trim(),
                DateTimeOffset.UtcNow,
                run.Revision));
            AddEvent(run, "ApprovalGranted", stage.Definition.Id, reviewer.Trim(), request.Rationale.Trim());
            run.ApprovedCheckpointJson = JsonSerializer.Serialize(run.Stages, OrchestrationJson.Options);
            run.State = RunState.Running;

            await ExecuteUntilGateAsync(database, run, cancellationToken);
            await PersistAsync(database, run, cancellationToken);
            return run;
        });
    }

    public async Task<OrchestrationRun> ReplanAsync(
        Guid runId,
        ReplanRunRequest request,
        string reviewer,
        CancellationToken cancellationToken)
    {
        var requirements = request.Requirements?.Trim();
        if (requirements is not null && (string.IsNullOrWhiteSpace(requirements) || requirements.Length > 4000))
        {
            throw new OrchestrationInputException("Updated requirements must contain 1-4000 characters.");
        }
        if (request.ChangedStageId is not null
            && (string.IsNullOrWhiteSpace(request.ChangedOutput) || request.ChangedOutput.Length > 4000))
        {
            throw new OrchestrationInputException("A changed upstream stage requires a non-empty output of at most 4000 characters.");
        }

        return await WithRunLockAsync(runId, async () =>
        {
            await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
            var run = await LoadRequiredAsync(database, runId, cancellationToken);
            var changedStageId = request.ChangedStageId?.Trim();
            var changedStage = changedStageId is null
                ? null
                : run.Stages.SingleOrDefault(stage => stage.Definition.Id == changedStageId);
            if (changedStageId is not null && changedStage is null)
            {
                throw new OrchestrationInputException($"Unknown changed stage '{changedStageId}'.");
            }

            var previousRevision = run.Revision;
            run.Revision++;
            if (requirements is not null)
            {
                run.Requirements = requirements;
            }
            run.HighImpact = run.Scenario == "ambiguous" || HasHighImpactTerms(run.Requirements);
            run.ApprovedCheckpointJson = null;

            var invalidatedStageIds = changedStageId is null
                ? run.Stages.Select(stage => stage.Definition.Id).ToHashSet(StringComparer.Ordinal)
                : GetDescendantStageIds(changedStageId);
            foreach (var stage in run.Stages.Where(stage => invalidatedStageIds.Contains(stage.Definition.Id)))
            {
                if (stage.Definition.Id == changedStageId)
                {
                    stage.Output = request.ChangedOutput;
                    stage.State = stage.Definition.RequiresApproval ? StageState.Pending : StageState.Completed;
                    stage.Error = null;
                    stage.Attempts = 0;
                    stage.Approved = false;
                    continue;
                }

                stage.State = StageState.Pending;
                stage.Approved = false;
                stage.Attempts = 0;
                stage.Output = null;
                stage.Error = null;
            }
            run.State = RunState.Running;
            var replanDetails = changedStageId is null
                ? $"Revision {previousRevision} superseded by revision {run.Revision}; all stage outputs and approvals invalidated."
                : $"Revision {previousRevision} superseded by revision {run.Revision}; output from '{changedStageId}' changed and {invalidatedStageIds.Count - 1} dependent stages invalidated.";
            AddEvent(run, "Replanned", changedStageId, reviewer.Trim(), replanDetails);

            await ExecuteUntilGateAsync(database, run, cancellationToken);
            await PersistAsync(database, run, cancellationToken);
            return run;
        });
    }

    public async Task<OrchestrationRun> RollbackAsync(Guid runId, string reviewer, CancellationToken cancellationToken) =>
        await WithRunLockAsync(runId, async () =>
        {
            await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
            var run = await LoadRequiredAsync(database, runId, cancellationToken);
            if (run.ApprovedCheckpointJson is null)
            {
                throw new OrchestrationConflictException("No approved checkpoint is available for rollback.");
            }

            RestoreCheckpoint(run);
            run.State = RunState.RolledBack;
            AddEvent(run, "Rollback", null, reviewer.Trim(), "Run state restored to the most recent approved checkpoint.");
            await PersistAsync(database, run, cancellationToken);
            return run;
        });

    public async Task<OrchestrationMetrics> GetMetricsAsync(CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var entities = await database.OrchestrationRuns.AsNoTracking().ToListAsync(cancellationToken);
        var runs = entities
            .Select(entity => JsonSerializer.Deserialize<OrchestrationRun>(entity.SnapshotJson, OrchestrationJson.Options))
            .Where(run => run is not null)
            .Cast<OrchestrationRun>()
            .ToList();
        var retries = runs.Sum(run => run.Events.Count(item => item.Type == "Retry"));
        var rollbacks = runs.Sum(run => run.Events.Count(item => item.Type == "Rollback"));
        var recoveredDurations = runs
            .SelectMany(run =>
            {
                var durations = new List<double>();
                DateTimeOffset? safeStoppedAt = null;
                foreach (var auditEvent in run.Events.OrderBy(item => item.Sequence))
                {
                    if (auditEvent.Type == "SafeStop")
                    {
                        safeStoppedAt = auditEvent.At;
                    }
                    else if (auditEvent.Type == "Rollback" && auditEvent.Actor != "system" && safeStoppedAt is not null)
                    {
                        durations.Add((auditEvent.At - safeStoppedAt.Value).TotalMilliseconds);
                        safeStoppedAt = null;
                    }
                }
                return durations;
            })
            .ToArray();
        var completedDurations = runs
            .Where(run => run.State == RunState.Completed)
            .Select(run => (run.UpdatedAt - run.CreatedAt).TotalMilliseconds)
            .ToArray();

        return new OrchestrationMetrics(
            runs.Count,
            runs.Count == 0 ? 0 : (double)runs.Count(run => run.State == RunState.Completed) / runs.Count,
            retries,
            runs.Count == 0 ? 0 : (double)retries / runs.Count,
            rollbacks,
            runs.Count == 0 ? 0 : (double)rollbacks / runs.Count,
            recoveredDurations.Length == 0 ? null : recoveredDurations.Average(),
            completedDurations.Length == 0 ? null : completedDurations.Average());
    }

    private async Task ExecuteUntilGateAsync(
        UrlDbContext database,
        OrchestrationRun run,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var ready = run.Stages
                .Where(stage => stage.State == StageState.Pending
                    && stage.Definition.Dependencies.All(dependency =>
                        run.Stages.Any(item => item.Definition.Id == dependency && item.State == StageState.Completed)))
                .ToArray();

            if (ready.Length == 0)
            {
                run.State = run.Stages.All(stage => stage.State == StageState.Completed)
                    ? RunState.Completed
                    : RunState.SafeStopped;
                if (run.State == RunState.Completed)
                {
                    AddEvent(run, "RunCompleted", null, "system", "All stages and exit gates completed.");
                }
                await PersistAsync(database, run, cancellationToken);
                return;
            }

            var approvalGate = ready.FirstOrDefault(stage => stage.Definition.RequiresApproval && !stage.Approved);
            if (approvalGate is not null)
            {
                approvalGate.State = StageState.AwaitingApproval;
                run.State = RunState.NeedsApproval;
                AddEvent(run, "ApprovalRequested", approvalGate.Definition.Id, "system",
                    run.HighImpact ? "High-impact change requires explicit human approval." : "Human checkpoint required before this stage.");
                await PersistAsync(database, run, cancellationToken);
                return;
            }

            run.State = RunState.Running;
            foreach (var stage in ready)
            {
                stage.State = StageState.Running;
                AddEvent(run, "StageStarted", stage.Definition.Id, "system", stage.Definition.EntryGate);
            }
            await PersistAsync(database, run, cancellationToken);

            var outcomes = await Task.WhenAll(ready.Select(stage => ExecuteWithPolicyAsync(run, stage, cancellationToken)));
            foreach (var outcome in outcomes)
            {
                foreach (var auditEvent in outcome.Events)
                {
                    run.Events.Add(auditEvent with { Sequence = run.Events.Count + 1 });
                }
                if (outcome.Stage.State == StageState.Completed)
                {
                    AddEvent(run, "StageCompleted", outcome.Stage.Definition.Id, "system", outcome.Stage.Definition.ExitGate);
                }
            }

            var failed = outcomes.FirstOrDefault(outcome => outcome.Stage.State == StageState.Failed);
            if (failed is not null)
            {
                AddEvent(run, "SafeStop", failed.Stage.Definition.Id, "system", "Stage exhausted bounded retries; execution halted for human review.");
                if (run.ApprovedCheckpointJson is not null)
                {
                    RestoreCheckpoint(run);
                    var restoredFailedStage = run.Stages.Single(stage => stage.Definition.Id == failed.Stage.Definition.Id);
                    restoredFailedStage.State = StageState.Failed;
                    restoredFailedStage.Attempts = failed.Stage.Attempts;
                    restoredFailedStage.Error = failed.Stage.Error;
                    AddEvent(run, "Rollback", failed.Stage.Definition.Id, "system", "Stage exhausted bounded retries; state restored to the last approved checkpoint.");
                }
                else
                {
                    AddEvent(run, "SafeStop", failed.Stage.Definition.Id, "system", "Stage exhausted bounded retries; no approved checkpoint was available.");
                }
                run.State = RunState.SafeStopped;
                await PersistAsync(database, run, cancellationToken);
                return;
            }

            await PersistAsync(database, run, cancellationToken);
        }
    }

    private async Task<StageOutcome> ExecuteWithPolicyAsync(
        OrchestrationRun run,
        StageSnapshot stage,
        CancellationToken cancellationToken)
    {
        var events = new List<AuditEvent>();
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            stage.Attempts = attempt;
            try
            {
                var execution = await executor.ExecuteAsync(
                    new StageContext(
                        run.Scenario,
                        run.Requirements,
                        run.Revision,
                        stage.Definition.Id,
                        run.Stages
                            .Where(upstream => upstream.State == StageState.Completed && upstream.Output is not null)
                            .ToDictionary(upstream => upstream.Definition.Id, upstream => upstream.Output!, StringComparer.Ordinal)),
                    cancellationToken);
                if (!execution.ExitGatePassed || string.IsNullOrWhiteSpace(execution.Output))
                {
                    throw new InvalidOperationException($"Exit gate failed: {stage.Definition.ExitGate}.");
                }

                stage.Output = execution.Output;
                stage.State = StageState.Completed;
                stage.Error = null;
                return new StageOutcome(stage, events);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                stage.Error = exception.Message;
                if (attempt < MaximumAttempts)
                {
                    events.Add(CreateEvent(run, "Retry", stage.Definition.Id, "system", $"Attempt {attempt} failed; retrying within the {MaximumAttempts}-attempt bound."));
                    continue;
                }

                if (stage.Definition.Id == "documentation")
                {
                    stage.Output = "Fallback documentation artifact: API and setup details require human completion.";
                    stage.State = StageState.Completed;
                    events.Add(CreateEvent(run, "FallbackApplied", stage.Definition.Id, "system", "Deterministic documentation fallback used after bounded retries."));
                    return new StageOutcome(stage, events);
                }

                stage.State = StageState.Failed;
            }
        }

        return new StageOutcome(stage, events);
    }

    private async Task<T> WithRunLockAsync<T>(Guid runId, Func<Task<T>> action)
    {
        var semaphore = _runLocks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task<OrchestrationRun?> LoadAsync(
        UrlDbContext database,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var entity = await database.OrchestrationRuns
            .SingleOrDefaultAsync(item => item.Id == runId.ToString("D"), cancellationToken);
        return entity is null
            ? null
            : JsonSerializer.Deserialize<OrchestrationRun>(entity.SnapshotJson, OrchestrationJson.Options);
    }

    private static async Task<OrchestrationRun> LoadRequiredAsync(
        UrlDbContext database,
        Guid runId,
        CancellationToken cancellationToken) =>
        await LoadAsync(database, runId, cancellationToken)
        ?? throw new OrchestrationRunNotFoundException();

    private static async Task PersistAsync(
        UrlDbContext database,
        OrchestrationRun run,
        CancellationToken cancellationToken)
    {
        run.UpdatedAt = DateTimeOffset.UtcNow;
        var entity = await database.OrchestrationRuns
            .SingleAsync(item => item.Id == run.Id.ToString("D"), cancellationToken);
        entity.SnapshotJson = JsonSerializer.Serialize(run, OrchestrationJson.Options);
        entity.UpdatedAt = run.UpdatedAt;
        var persistedEventCount = await database.OrchestrationAuditEvents
            .CountAsync(auditEvent => auditEvent.RunId == entity.Id, cancellationToken);
        foreach (var auditEvent in run.Events.Where(auditEvent => auditEvent.Sequence > persistedEventCount))
        {
            database.OrchestrationAuditEvents.Add(new OrchestrationAuditEventEntity
            {
                RunId = entity.Id,
                Sequence = auditEvent.Sequence,
                At = auditEvent.At,
                Type = auditEvent.Type,
                StageId = auditEvent.StageId,
                Actor = auditEvent.Actor,
                Details = auditEvent.Details,
                Revision = auditEvent.Revision
            });
        }
        await database.SaveChangesAsync(cancellationToken);
    }

    private static void RestoreCheckpoint(OrchestrationRun run)
    {
        var checkpoint = JsonSerializer.Deserialize<List<StageSnapshot>>(
            run.ApprovedCheckpointJson!, OrchestrationJson.Options);
        if (checkpoint is null)
        {
            throw new InvalidOperationException("Approved checkpoint could not be read.");
        }
        run.Stages = checkpoint;
    }

    private static AuditEvent CreateEvent(
        OrchestrationRun run,
        string type,
        string? stageId,
        string actor,
        string details) => new(
            0,
            DateTimeOffset.UtcNow,
            type,
            stageId,
            actor,
            details,
            run.Revision);

    private static void AddEvent(OrchestrationRun run, string type, string? stageId, string actor, string details)
    {
        var auditEvent = CreateEvent(run, type, stageId, actor, details);
        run.Events.Add(auditEvent with { Sequence = run.Events.Count + 1 });
    }

    private static string ScenarioRequirement(string scenario) => scenario switch
    {
        "greenfield" => "Build a URL shortener with unique links, HTTP redirects, expiry, click analytics, and operational health checks.",
        "brownfield" => "Enhance the existing URL shortener with link expiry and privacy-minimized analytics without changing redirect behavior.",
        _ => "Make shortened links safer and analytics better."
    };

    private static HashSet<string> GetDescendantStageIds(string changedStageId)
    {
        var descendants = new HashSet<string>(StringComparer.Ordinal) { changedStageId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var stage in StageGraph)
            {
                if (!descendants.Contains(stage.Id) && stage.Dependencies.Any(descendants.Contains))
                {
                    descendants.Add(stage.Id);
                    changed = true;
                }
            }
        }
        return descendants;
    }

    private static bool HasHighImpactTerms(string requirements) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            requirements,
            @"\b(delete|deletion|pii|payment|credential|production|retention|personal data)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private sealed record StageOutcome(StageSnapshot Stage, List<AuditEvent> Events);
}