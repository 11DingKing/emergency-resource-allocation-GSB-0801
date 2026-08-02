namespace EmergencyAllocation.Domain.Dtos;

public sealed record SolveRequestDto(
    string InputVersion,
    string? Reason,
    bool? ForceReconsider);

public sealed record RoadInterruptRequestDto(
    string RoadCode,
    string? Reason,
    string InputVersion);

public sealed record RoadReopenRequestDto(
    string RoadCode,
    string? Reason,
    string InputVersion);

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
    int TotalCostMinutes,
    int AssignedCount,
    int UnassignedCount,
    string? FailureReason,
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
