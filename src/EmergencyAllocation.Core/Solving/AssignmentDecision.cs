namespace EmergencyAllocation.Core.Solving;

public sealed record AssignmentDecision(
    string TaskId,
    string? TeamId,
    string? VehicleId,
    IReadOnlyList<string> RouteNodes,
    int EstimatedArrivalMinutes,
    int EstimatedCompletionMinutes,
    AssignmentKind Kind,
    string? PreemptionReason,
    int OrderIndex);
