using EmergencyDispatch.Domain;

namespace EmergencyDispatch.Api.Contracts;

public sealed record SolveRequestDto(string InputVersion, string Kind, string? Reason);

public sealed record ReasonDto(string Code, string Message);

public sealed record AssignmentDto(
    string TaskCode,
    string TaskTitle,
    string TeamCode,
    string TeamName,
    string VehicleName,
    decimal VehicleHeightMeters,
    string? RoadCode,
    string? RoadName,
    int EtaMinutes,
    int Cost,
    bool IsPreemption,
    IReadOnlyList<ReasonDto> Reasons);

public sealed record UnassignedDto(string TaskCode, string TaskTitle, IReadOnlyList<ReasonDto> Reasons);

public sealed record PlanDto(
    Guid PlanId,
    long PlanVersion,
    string InputVersion,
    string Kind,
    string Status,
    string? Reason,
    int TotalCostMinutes,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<AssignmentDto> Assignments,
    IReadOnlyList<UnassignedDto> Unassigned);

public sealed record TeamDto(Guid Id, string Code, string Name, IReadOnlyList<string> Capabilities, string VehicleName, decimal VehicleHeightMeters);

public sealed record RoadDto(Guid Id, string Code, string Name, decimal MaxVehicleHeightMeters, int TravelMinutes, bool IsBlocked);

public sealed record TaskDto(
    Guid Id,
    string Code,
    string Title,
    string Kind,
    IReadOnlyList<string> RequiredCapabilities,
    int? DeadlineMinutes,
    int DurationMinutes,
    string Danger,
    string Status,
    string? CurrentTeamCode);

public sealed record RoadUpdateDto(bool IsBlocked);

public sealed record DangerUpdateDto(string Level, string? Reason);

public static class PlanMapper
{
    public static PlanDto ToDto(AllocationPlan p) => new(
        p.Id,
        p.PlanVersion,
        p.InputVersion,
        p.Kind.ToString().ToLowerInvariant(),
        p.Status.ToString().ToLowerInvariant(),
        p.Reason,
        p.TotalCostMinutes,
        p.CreatedAtUtc,
        p.Assignments.OrderBy(a => a.Task!.Code, StringComparer.Ordinal).Select(a => new AssignmentDto(
            a.Task!.Code,
            a.Task!.Title,
            a.Team!.Code,
            a.Team!.Name,
            a.Team!.Vehicle?.Name ?? "无车辆",
            a.Team!.Vehicle?.HeightMeters ?? 0m,
            a.Road?.Code,
            a.Road?.Name,
            a.EtaMinutes,
            a.Cost,
            a.IsPreemption,
            a.Reasons.Select(r => new ReasonDto(r.Code, r.Message)).ToArray())).ToArray(),
        p.Unassigned.OrderBy(u => u.Task!.Code, StringComparer.Ordinal).Select(u => new UnassignedDto(
            u.Task!.Code,
            u.Task!.Title,
            u.Reasons.Select(r => new ReasonDto(r.Code, r.Message)).ToArray())).ToArray());
}
