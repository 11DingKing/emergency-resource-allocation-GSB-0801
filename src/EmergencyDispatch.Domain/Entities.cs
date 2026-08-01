namespace EmergencyDispatch.Domain;

/// <summary>救援队。能力为稳定字符串集合（如 water_rescue / first_aid / slope_patrol）。</summary>
public class Team
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public List<string> Capabilities { get; set; } = new();
    public Vehicle? Vehicle { get; set; }
}

/// <summary>车辆，与队伍一一绑定。高度（米）用于道路限高判定。</summary>
public class Vehicle
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public decimal HeightMeters { get; set; }
    public Guid TeamId { get; set; }
    public Team? Team { get; set; }
}

/// <summary>路段。所有时长统一为分钟。xmin 作为乐观并发令牌，感知求解中途的快照变更。</summary>
public class RoadSegment
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public decimal MaxVehicleHeightMeters { get; set; }
    public int TravelMinutes { get; set; }
    public bool IsBlocked { get; set; }
}

/// <summary>调度任务。DeadlineMinutes 为最大到达分钟数，null 表示无到达时限。</summary>
public class DispatchTask
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string Title { get; set; }
    public TaskKind Kind { get; set; }
    public List<string> RequiredCapabilities { get; set; } = new();
    public int? DeadlineMinutes { get; set; }
    public int DurationMinutes { get; set; }
    public DangerLevel Danger { get; set; } = DangerLevel.Standard;
    public DispatchTaskStatus Status { get; set; } = DispatchTaskStatus.Pending;
    public Guid? CurrentTeamId { get; set; }
    public Team? CurrentTeam { get; set; }
}

/// <summary>
/// 分配版本。InputVersion 由调用方提供并参与幂等控制（唯一约束）；
/// 同一输入版本的重复/并发提交永远返回同一份方案。
/// </summary>
public class AllocationPlan
{
    public Guid Id { get; set; }
    public long PlanVersion { get; set; }
    public required string InputVersion { get; set; }
    public PlanKind Kind { get; set; }
    public PlanStatus Status { get; set; }
    public string? Reason { get; set; }
    public int TotalCostMinutes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? SupersedesPlanId { get; set; }
    public List<Assignment> Assignments { get; set; } = new();
    public List<UnassignedTask> Unassigned { get; set; } = new();
}

/// <summary>一条分配决定。RoadId 为 null 表示任务执行中、无需再上路（不可抢占锁定）。</summary>
public class Assignment
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public Guid TaskId { get; set; }
    public DispatchTask? Task { get; set; }
    public Guid TeamId { get; set; }
    public Team? Team { get; set; }
    public Guid? RoadId { get; set; }
    public RoadSegment? Road { get; set; }
    public int EtaMinutes { get; set; }
    public int Cost { get; set; }
    public bool IsPreemption { get; set; }
    public List<ReasonEntry> Reasons { get; set; } = new();
}

/// <summary>未能分配的任务及其原因（只出现在 Infeasible 方案中）。</summary>
public class UnassignedTask
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public Guid TaskId { get; set; }
    public DispatchTask? Task { get; set; }
    public List<ReasonEntry> Reasons { get; set; } = new();
}

/// <summary>结构化原因：稳定机器码 + 人类可读说明。求解、审计解释与差异输出共用同一份数据。</summary>
public class ReasonEntry
{
    public required string Code { get; set; }
    public required string Message { get; set; }
}
