namespace EmergencyAllocation.Core.Solving;

public sealed record RoadState(
    string Id,
    string Name,
    string FromNode,
    string ToNode,
    decimal HeightLimitMeters,
    int TravelTimeMinutes,
    bool IsOpen,
    string? ClosedByEventId,
    string? ClosedByReason);
