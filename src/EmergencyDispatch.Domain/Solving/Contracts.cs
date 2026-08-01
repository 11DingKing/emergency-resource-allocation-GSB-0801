namespace EmergencyDispatch.Domain.Solving;

/// <summary>
/// 求解器契约。求解器只依赖本快照模型，与 API、数据库完全隔离；
/// 生产注入确定性求解器，测试可注入任意确定性实现。
/// </summary>
public interface IAllocationSolver
{
    SolveResult Solve(SolveInput input);
}

public sealed record TeamSnapshot(
    Guid Id,
    string Code,
    string Name,
    IReadOnlySet<string> Capabilities,
    string VehicleName,
    decimal VehicleHeightMeters);

public sealed record TaskSnapshot(
    Guid Id,
    string Code,
    string Title,
    TaskKind Kind,
    IReadOnlySet<string> RequiredCapabilities,
    int? DeadlineMinutes,
    int DurationMinutes,
    DangerLevel Danger,
    DispatchTaskStatus Status,
    Guid? IncumbentTeamId);

public sealed record RoadSnapshot(
    Guid Id,
    string Code,
    string Name,
    decimal MaxVehicleHeightMeters,
    int TravelMinutes,
    bool IsBlocked);

/// <summary>
/// AllowPreemption 由编排层按规则计算：仅当重排且存在生命危险等级上升（Critical）的生命安全任务时为 true。
/// </summary>
public sealed record SolveOptions(bool AllowPreemption, string? PreemptionReason);

public sealed record SolveInput(
    IReadOnlyList<TeamSnapshot> Teams,
    IReadOnlyList<TaskSnapshot> Tasks,
    IReadOnlyList<RoadSnapshot> Roads,
    SolveOptions Options);

public sealed record AssignmentDecision(
    Guid TaskId,
    Guid TeamId,
    Guid? RoadId,
    int EtaMinutes,
    int Cost,
    bool IsPreemption,
    IReadOnlyList<ReasonEntry> Reasons);

public sealed record UnassignedDecision(Guid TaskId, IReadOnlyList<ReasonEntry> Reasons);

public sealed record SolveResult(
    bool IsFeasible,
    IReadOnlyList<AssignmentDecision> Assignments,
    IReadOnlyList<UnassignedDecision> Unassigned,
    int TotalCostMinutes);

/// <summary>稳定原因码，贯穿 tie-break、不可分配解释、审计与差异输出。</summary>
public static class ReasonCodes
{
    public const string MinCost = "min_cost";
    public const string RouteSelected = "route_selected";
    public const string TieBreakTeamCode = "tie_break_team_code";
    public const string CostHigher = "cost_higher";
    public const string TeamBusy = "team_busy";
    public const string CapabilityMissing = "capability_missing";
    public const string HeightExceeded = "height_exceeded";
    public const string RoadBlocked = "road_blocked";
    public const string NoRouteForTeam = "no_route_for_team";
    public const string DeadlineExceeded = "deadline_exceeded";
    public const string ResourceConflict = "resource_conflict";
    public const string LockedInProgress = "locked_in_progress";
    public const string PreemptionDangerEscalation = "preemption_danger_escalation";
}
