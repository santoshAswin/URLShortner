using System.Text.Json;
using System.Text.Json.Serialization;

namespace UrlShortener.Api.Orchestration;

public sealed class OrchestrationRunEntity
{
    public required string Id { get; set; }
    public required string Scenario { get; set; }
    public required string SnapshotJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<OrchestrationAuditEventEntity> AuditEvents { get; set; } = [];
}

public sealed class OrchestrationAuditEventEntity
{
    public long Id { get; set; }
    public required string RunId { get; set; }
    public int Sequence { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Type { get; set; }
    public string? StageId { get; set; }
    public required string Actor { get; set; }
    public required string Details { get; set; }
    public int Revision { get; set; }

    public OrchestrationRunEntity Run { get; set; } = null!;
}

public sealed record CreateRunRequest(string Scenario, string? Requirements = null);
public sealed record ApproveStageRequest(string StageId, string Rationale);
public sealed record ReplanRunRequest(
    string? Requirements = null,
    string? ChangedStageId = null,
    string? ChangedOutput = null);
public sealed record DecisionRecord(string StageId, string Approver, string Rationale, DateTimeOffset DecidedAt, int Revision);
public sealed record AuditEvent(int Sequence, DateTimeOffset At, string Type, string? StageId, string Actor, string Details, int Revision);
public sealed record StageContext(
    string Scenario,
    string Requirements,
    int Revision,
    string StageId,
    IReadOnlyDictionary<string, string> UpstreamOutputs);
public sealed record StageDefinition(string Id, string Name, string EntryGate, string ExitGate, string[] Dependencies, bool RequiresApproval = false);
public sealed record StageExecution(string Output, bool ExitGatePassed = true);
public sealed record OrchestrationMetrics(
    int TotalRuns,
    double SuccessRate,
    int RetryCount,
    double RetryFrequency,
    int RollbackCount,
    double RollbackFrequency,
    double? MeanTimeToRecoveryMilliseconds,
    double? MeanEndToEndLatencyMilliseconds);

public enum StageState
{
    Pending,
    Running,
    AwaitingApproval,
    Completed,
    Failed,
    RolledBack
}

public enum RunState
{
    Running,
    NeedsApproval,
    Completed,
    SafeStopped,
    RolledBack
}

public sealed class StageSnapshot
{
    public required StageDefinition Definition { get; set; }
    public StageState State { get; set; } = StageState.Pending;
    public bool Approved { get; set; }
    public int Attempts { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
}

public sealed class OrchestrationRun
{
    public Guid Id { get; set; }
    public required string Scenario { get; set; }
    public required string Requirements { get; set; }
    public int Revision { get; set; } = 1;
    public bool HighImpact { get; set; }
    public RunState State { get; set; } = RunState.Running;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<StageSnapshot> Stages { get; set; } = [];
    public List<DecisionRecord> Decisions { get; set; } = [];
    public List<AuditEvent> Events { get; set; } = [];
    public string? ApprovedCheckpointJson { get; set; }
}

public interface IStageExecutor
{
    Task<StageExecution> ExecuteAsync(StageContext context, CancellationToken cancellationToken);
}

public sealed class DeterministicStageExecutor : IStageExecutor
{
    public Task<StageExecution> ExecuteAsync(StageContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = context.StageId switch
        {
            "requirements" => $"Normalized requirement: {context.Requirements}\nAcceptance: create and resolve links, persist click counts, honor expiry, and validate inputs.",
            "architecture" => "Design: ASP.NET Core minimal API; SQLite persistence; unique code index; privacy-minimized click events; bounded write rate.",
            "risk-review" => context.Scenario == "ambiguous"
                ? $"Risk review: high impact. Assumptions recorded; confirm retention, abuse controls, and expiry semantics before release. Design input: {context.UpstreamOutputs.GetValueOrDefault("architecture", "not provided")}"
                : $"Risk review: HTTP(S)-only destinations, no IP collection, bounded write rate, and human approvals before implementation and release. Design input: {context.UpstreamOutputs.GetValueOrDefault("architecture", "not provided")}",
            "implementation" => "Implementation artifact: API/data changes are represented as a reviewable proposal. No repository files are changed by this worker.",
            "tests" => "Validation artifact: unit, integration, security, expiry, collision, and persistence checks are required. Execution is owned by the repository test gate.",
            "documentation" => $"Documentation artifact: setup and API contracts for revision {context.Revision}; scenario {context.Scenario}.",
            "release" => "Release readiness: artifacts and validation evidence assembled; deployment remains a human-owned action.",
            _ => throw new InvalidOperationException($"Unknown stage '{context.StageId}'.")
        };
        return Task.FromResult(new StageExecution(output));
    }
}

public sealed class OrchestrationInputException(string message) : Exception(message);
public sealed class OrchestrationRunNotFoundException() : Exception;
public sealed class OrchestrationConflictException(string message) : Exception(message);

internal static class OrchestrationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}