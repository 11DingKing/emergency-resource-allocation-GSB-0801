namespace EmergencyAllocation.Infrastructure.Services.Contracts;

public sealed record InitialSolveRequest(string InputVersion);

public sealed record RearrangeRequest(
    string InputVersion,
    Guid PreviousVersionId,
    bool DangerLevelRaised);

public sealed record RoadUpdateRequest(bool? IsOpen);

public sealed record TaskStateUpdateRequest(
    EmergencyAllocation.Core.TaskStatus Status,
    string? AssignedTeamId,
    string? CurrentNode);

public sealed record AssignmentDto(
    string TaskId,
    string? TeamId,
    string? VehicleId,
    IReadOnlyList<string> RouteNodes,
    int EstimatedArrivalMinutes,
    int EstimatedCompletionMinutes,
    string Kind,
    string? PreemptionReason,
    int OrderIndex);

public sealed record ExplanationDto(
    string Kind,
    string RuleCode,
    string Message,
    string? RelatedTaskId,
    string? RelatedTeamId);

public sealed record AllocationResult(
    Guid VersionId,
    string InputVersion,
    bool IsFeasible,
    string? NoFeasibleReason,
    long TotalCost,
    string SolverVersion,
    DateTimeOffset CreatedAt,
    string SnapshotHash,
    IReadOnlyList<AssignmentDto> Assignments,
    IReadOnlyList<ExplanationDto> Explanations);
