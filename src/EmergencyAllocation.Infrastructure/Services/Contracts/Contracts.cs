namespace EmergencyAllocation.Infrastructure.Services.Contracts;

public sealed record InitialSolveRequest(
    string InputVersion,
    string? ExpectedRoadSnapshotHash = null,
    string? ExpectedTaskSnapshotHash = null,
    string? ExpectedTeamSnapshotHash = null,
    string? ExpectedVehicleSnapshotHash = null,
    string? TriggeringRoadEventId = null);

public sealed record RearrangeRequest(
    string InputVersion,
    Guid PreviousVersionId,
    bool DangerLevelRaised,
    string? ExpectedRoadSnapshotHash = null,
    string? ExpectedTaskSnapshotHash = null,
    string? ExpectedTeamSnapshotHash = null,
    string? ExpectedVehicleSnapshotHash = null,
    string? TriggeringRoadEventId = null);

public sealed record RoadEventRequest(
    string EventId,
    bool IsOpen,
    string Reason,
    string? RecordedBy = null);

public sealed record RoadUpdateRequest(bool? IsOpen);

public sealed record TaskStateUpdateRequest(
    EmergencyAllocation.Core.TaskStatus? Status = null,
    string? AssignedTeamId = null,
    string? CurrentNode = null,
    EmergencyAllocation.Core.DangerLevel? DangerLevel = null);

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

public sealed record SnapshotHashesDto(
    string Combined,
    string Roads,
    string Tasks,
    string Teams,
    string Vehicles);

public sealed record FieldDiffDto(
    string Field,
    string? ExpectedHash,
    string? ActualHash,
    string Message,
    IReadOnlyList<string> RelatedEntities);

public sealed record SnapshotConflictResponse(
    string InputVersion,
    string ConflictType,
    string Message,
    SnapshotHashesDto RequestedHashes,
    SnapshotHashesDto? CommittedHashes,
    SnapshotHashesDto CurrentHashes,
    IReadOnlyList<FieldDiffDto> FieldDiffs);

public sealed record AllocationResult(
    Guid VersionId,
    string InputVersion,
    bool IsFeasible,
    string? NoFeasibleReason,
    long TotalCost,
    string SolverVersion,
    DateTimeOffset CreatedAt,
    string SnapshotHash,
    SnapshotHashesDto SnapshotHashes,
    string? TriggeringRoadEventId,
    Guid? PreviousVersionId,
    IReadOnlyList<AssignmentDto> Assignments,
    IReadOnlyList<ExplanationDto> Explanations);

public sealed record AssignmentChangeDto(
    string TaskId,
    string? FromTeamId,
    string? ToTeamId,
    IReadOnlyList<string>? FromRoute,
    IReadOnlyList<string>? ToRoute,
    int FromArrivalMinutes,
    int ToArrivalMinutes,
    string FromKind,
    string ToKind,
    string ChangeType,
    string? PreemptionReason);

public sealed record SnapshotFieldChangeDto(
    string Field,
    string? FromHash,
    string? ToHash,
    bool Changed);

public sealed record VersionDiffDto(
    Guid FromVersionId,
    string FromInputVersion,
    Guid ToVersionId,
    string ToInputVersion,
    long FromCost,
    long ToCost,
    long CostDelta,
    IReadOnlyList<AssignmentChangeDto> AssignmentChanges,
    IReadOnlyList<SnapshotFieldChangeDto> SnapshotChanges,
    string? TriggeringRoadEventId,
    IReadOnlyList<string> ChangeReasons);
