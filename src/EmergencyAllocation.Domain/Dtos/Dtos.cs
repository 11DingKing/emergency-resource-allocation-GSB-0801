namespace EmergencyAllocation.Domain.Dtos;

public sealed record SolveRequestDto(
    string InputVersion,
    string? Reason = null,
    bool? ForceReconsider = null,
    string? RoadEventId = null,
    string? ExpectedRoadDigest = null,
    string? ExpectedTaskDigest = null,
    string? ExpectedTeamDigest = null,
    string? ExpectedVehicleDigest = null,
    long? ExpectedRoadSnapshotVersion = null);

public sealed record RoadInterruptRequestDto(
    string RoadCode,
    string? Reason,
    string InputVersion,
    string? EventId = null,
    string? RecordedBy = null);

public sealed record RoadReopenRequestDto(
    string RoadCode,
    string? Reason,
    string InputVersion,
    string? EventId = null,
    string? RecordedBy = null);

public sealed record TaskEscalationRequestDto(
    string TaskCode,
    string TargetSeverity,
    string InputVersion,
    string? Reason = null);

public sealed record AssignmentDto(
    Guid TaskId,
    string TaskCode,
    string TaskTitle,
    Guid? TeamId,
    string? TeamCode,
    Guid? VehicleId,
    string? VehicleCode,
    string Decision,
    int EstimatedTravelMinutes,
    int EstimatedTotalMinutes,
    bool MeetsDeadline,
    IReadOnlyList<string> RouteNodeIds,
    string? Reason,
    bool Preempted,
    Guid? PreviousTeamId,
    Guid? PreviousVehicleId);

public sealed record AuditEntryDto(
    int Order,
    string Kind,
    string? TaskCode,
    string? TeamCode,
    string? VehicleCode,
    string? RoadCode,
    string? RoadEventId,
    string Message);

public sealed record SnapshotDigestDto(
    long RoadSnapshotVersion,
    string Road,
    string Task,
    string Team,
    string Vehicle);

public sealed record FieldDiffDto(
    string Field,
    string? Expected,
    string? Actual,
    string Detail);

public sealed record SnapshotConflictDto(
    string IdempotencyKey,
    string ExistingStatus,
    string ExistingRequestDigest,
    string CurrentRequestDigest,
    IReadOnlyList<FieldDiffDto> FieldDiffs,
    string Message);

public sealed record AllocationVersionDto(
    Guid Id,
    string Operation,
    string InputVersion,
    string IdempotencyKey,
    string Status,
    long Sequence,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CommittedAt,
    long? RoadSnapshotVersion,
    string? RoadDigest,
    string? TaskDigest,
    string? TeamDigest,
    string? VehicleDigest,
    string? RequestPayloadDigest,
    string? TriggeringRoadEventId,
    int TotalCostMinutes,
    int AssignedCount,
    int UnassignedCount,
    string? FailureReason,
    SnapshotDigestDto? CurrentSnapshots,
    IReadOnlyList<AssignmentDto> Assignments,
    IReadOnlyList<AuditEntryDto> Audit);

public sealed record TeamDto(
    Guid Id, string Code, string Name, string BaseNodeId, bool IsAvailable,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<VehicleDto> Vehicles);

public sealed record VehicleDto(
    Guid Id, string Code, string Name, double HeightMeters,
    double AverageSpeedMetersPerMinute, string Kind, bool IsAvailable);

public sealed record TaskDto(
    Guid Id, string Code, string Title, string LocationNodeId,
    string Severity, string Status, int DurationMinutes, int? DeadlineMinutes,
    int SeverityVersion, Guid? AssignedTeamId, Guid? AssignedVehicleId,
    DateTimeOffset? StartedAt, IReadOnlyList<string> RequiredCapabilities);

public sealed record RoadSegmentDto(
    Guid Id, string Code, string FromNodeId, string ToNodeId,
    int TravelTimeMinutes, double? HeightLimitMeters, bool IsOpen,
    long RoadSnapshotVersion, DateTimeOffset UpdatedAt, string? InterruptionReason);

public sealed record RoadEventDto(
    Guid Id,
    string EventId,
    string RoadCode,
    string Kind,
    string? Reason,
    long RoadSnapshotVersionBefore,
    long RoadSnapshotVersionAfter,
    DateTimeOffset OccurredAt,
    string? RecordedBy);
