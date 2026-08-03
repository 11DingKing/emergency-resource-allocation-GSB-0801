namespace EmergencyAllocation.Core.Solving;

public sealed record RouteInfo(
    IReadOnlyList<string> Nodes,
    int TravelTimeMinutes,
    bool IsFeasible,
    string? BlockReason);
