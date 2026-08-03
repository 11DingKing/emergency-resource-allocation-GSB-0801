namespace EmergencyAllocation.Core.Solving;

public sealed record TeamState(
    string Id,
    string Name,
    string VehicleId,
    decimal VehicleHeightMeters,
    string CurrentNode,
    bool IsAvailable,
    IReadOnlySet<string> Capabilities,
    string? CurrentTaskId);
