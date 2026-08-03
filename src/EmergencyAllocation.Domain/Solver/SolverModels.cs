namespace EmergencyAllocation.Domain.Solver;

public sealed record SolverRoad(
    string Code,
    string FromNodeId,
    string ToNodeId,
    int TravelTimeMinutes,
    double? HeightLimitMeters,
    bool IsOpen,
    long SnapshotVersion);

public sealed record SolverVehicle(
    Guid Id,
    string Code,
    Guid TeamId,
    double HeightMeters,
    double AverageSpeedMetersPerMinute,
    bool IsAvailable);

public sealed record SolverTeam(
    Guid Id,
    string Code,
    string BaseNodeId,
    bool IsAvailable,
    IReadOnlySet<string> Capabilities,
    IReadOnlyList<SolverVehicle> Vehicles);

public sealed record SolverTask(
    Guid Id,
    string Code,
    string Title,
    string LocationNodeId,
    TaskSeverity Severity,
    int SeverityVersion,
    TaskStatus Status,
    int DurationMinutes,
    int? DeadlineMinutes,
    IReadOnlySet<string> RequiredCapabilities,
    Guid? AssignedTeamId,
    Guid? AssignedVehicleId,
    bool HasStarted);

public sealed record SolverRouteStep(string RoadCode, string FromNodeId, string ToNodeId, int TravelTimeMinutes);

public sealed record SolverRoute(
    IReadOnlyList<SolverRouteStep> Steps,
    int TotalTravelMinutes,
    IReadOnlyList<string> NodeIds);

public sealed record SolverAssignment(
    Guid TaskId,
    string TaskCode,
    Guid? TeamId,
    string? TeamCode,
    Guid? VehicleId,
    string? VehicleCode,
    AllocationDecision Decision,
    int EstimatedTravelMinutes,
    int EstimatedTotalMinutes,
    bool MeetsDeadline,
    SolverRoute? Route,
    string? Reason,
    bool Preempted,
    Guid? PreviousTeamId,
    Guid? PreviousVehicleId);

public sealed record SolverAuditEntry(
    int Order,
    string Kind,
    string? TaskCode,
    string? TeamCode,
    string? VehicleCode,
    string? RoadCode,
    string Message);

public sealed record SolverResult(
    bool Feasible,
    int TotalCostMinutes,
    IReadOnlyList<SolverAssignment> Assignments,
    IReadOnlyList<SolverAuditEntry> Audit,
    long RoadSnapshotVersion);

public sealed record SolverRequest(
    IReadOnlyList<SolverTeam> Teams,
    IReadOnlyList<SolverTask> Tasks,
    IReadOnlyList<SolverRoad> Roads,
    long RoadSnapshotVersion,
    string Operation,
    bool AllowReassign,
    string? Reason);
