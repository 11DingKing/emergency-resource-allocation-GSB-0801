namespace EmergencyAllocation.Core.Solving;

public sealed record TaskState(
    string Id,
    string Name,
    string LocationNode,
    int RequiredArrivalMinutes,
    int DurationMinutes,
    DangerLevel DangerLevel,
    TaskStatus Status,
    string? AssignedTeamId,
    IReadOnlySet<string> RequiredCapabilities);
