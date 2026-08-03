using EmergencyDispatch.Domain;
using EmergencyDispatch.Domain.Solving;
using EmergencyDispatch.Solver;
using Xunit;

namespace EmergencyDispatch.Tests;

/// <summary>
/// 求解器规则测试：能力集合、限高、道路中断、时限、锁定/抢占、确定性 tie-break、不可分配原因。
/// </summary>
public class SolverTests
{
    private static readonly Guid TeamA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid TeamB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid TeamC = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid RoadR1 = Guid.Parse("00000000-0000-0000-0000-000000000101");
    private static readonly Guid RoadR2 = Guid.Parse("00000000-0000-0000-0000-000000000102");
    private static readonly Guid TaskT1 = Guid.Parse("00000000-0000-0000-0000-000000000201");
    private static readonly Guid TaskT2 = Guid.Parse("00000000-0000-0000-0000-000000000202");

    private static TeamSnapshot Team(string code, Guid id, decimal height, params string[] caps) =>
        new(id, code, code + "队", caps.ToHashSet(StringComparer.Ordinal), "车" + code, height);

    private static RoadSnapshot Road(Guid id, string code, decimal limit, int minutes, bool blocked = false) =>
        new(id, code, code + "路", limit, minutes, blocked);

    private static TaskSnapshot Task(Guid id, string code, string[] caps, int? deadline,
        DispatchTaskStatus status = DispatchTaskStatus.Pending, Guid? incumbent = null,
        TaskKind kind = TaskKind.LifeSafety, DangerLevel danger = DangerLevel.Standard) =>
        new(id, code, code + "任务", kind, caps.ToHashSet(StringComparer.Ordinal), deadline, 60, danger, status, incumbent);

    private static SolveInput SeedLikeInput(bool r1Blocked = false, bool r2Blocked = false, SolveOptions? options = null) =>
        new(
            Teams:
            [
                Team("A", TeamA, 3.4m, "water_rescue", "first_aid"),
                Team("B", TeamB, 2.6m, "slope_patrol", "first_aid"),
                Team("C", TeamC, 3.0m, "water_rescue", "first_aid", "slope_patrol"),
            ],
            Tasks:
            [
                Task(TaskT1, "T1", ["first_aid"], 35),
                Task(TaskT2, "T2", ["slope_patrol"], null, DispatchTaskStatus.InProgress, TeamB, TaskKind.SlopeOperation),
            ],
            Roads:
            [
                Road(RoadR1, "R1", 3.2m, 34, r1Blocked),
                Road(RoadR2, "R2", 4.0m, 30, r2Blocked),
            ],
            Options: options ?? new SolveOptions(false, null));

    private readonly DeterministicAllocationSolver _solver = new();

    [Fact]
    public void 初始场景_T1由tie_break分给A_T2锁定给B()
    {
        var result = _solver.Solve(SeedLikeInput());

        Assert.True(result.IsFeasible);
        var t1 = Assert.Single(result.Assignments, a => a.TaskId == TaskT1);
        Assert.Equal(TeamA, t1.TeamId);          // A 与 C 同为 30 分钟，字典序决胜取 A
        Assert.Equal(RoadR2, t1.RoadId);
        Assert.Equal(30, t1.EtaMinutes);
        Assert.Contains(t1.Reasons, r => r.Code == ReasonCodes.TieBreakTeamCode);
        Assert.Contains(t1.Reasons, r => r.Code == ReasonCodes.TeamBusy); // B 在执行 T2

        var t2 = Assert.Single(result.Assignments, a => a.TaskId == TaskT2);
        Assert.Equal(TeamB, t2.TeamId);
        Assert.False(t2.IsPreemption);
        Assert.Contains(t2.Reasons, r => r.Code == ReasonCodes.LockedInProgress);
        Assert.Equal(30, result.TotalCostMinutes);
    }

    [Fact]
    public void 输入顺序不影响结果_确定性()
    {
        var input = SeedLikeInput();
        var shuffled = input with
        {
            Teams = input.Teams.Reverse().ToArray(),
            Tasks = input.Tasks.Reverse().ToArray(),
            Roads = input.Roads.Reverse().ToArray()
        };

        var a = _solver.Solve(input);
        var b = _solver.Solve(shuffled);

        Assert.Equal(
            a.Assignments.Select(x => (x.TaskId, x.TeamId, x.RoadId, x.EtaMinutes)).OrderBy(x => x.TaskId),
            b.Assignments.Select(x => (x.TaskId, x.TeamId, x.RoadId, x.EtaMinutes)).OrderBy(x => x.TaskId));
    }

    [Fact]
    public void R2中断后_T1改由C经R1_A因限高与中断不可达()
    {
        var result = _solver.Solve(SeedLikeInput(r2Blocked: true));

        Assert.True(result.IsFeasible);
        var t1 = Assert.Single(result.Assignments, a => a.TaskId == TaskT1);
        Assert.Equal(TeamC, t1.TeamId);
        Assert.Equal(RoadR1, t1.RoadId);
        Assert.Equal(34, t1.EtaMinutes);
        Assert.Contains(t1.Reasons, r => r.Code == ReasonCodes.HeightExceeded); // A 超 R1 限高
        Assert.Contains(t1.Reasons, r => r.Code == ReasonCodes.RoadBlocked);    // R2 中断
        Assert.Contains(t1.Reasons, r => r.Code == ReasonCodes.NoRouteForTeam); // A 无路可走

        Assert.Contains(result.Assignments, a => a.TaskId == TaskT2 && a.TeamId == TeamB);
    }

    [Fact]
    public void 全部道路中断_无可行解_原因完整且无半套分配()
    {
        var result = _solver.Solve(SeedLikeInput(r1Blocked: true, r2Blocked: true));

        Assert.False(result.IsFeasible);
        Assert.Empty(result.Assignments); // 绝不输出部分分配
        var t1 = Assert.Single(result.Unassigned, u => u.TaskId == TaskT1);
        Assert.Contains(t1.Reasons, r => r.Code == ReasonCodes.RoadBlocked);
        Assert.Contains(t1.Reasons, r => r.Code == ReasonCodes.NoRouteForTeam);
        Assert.Contains(t1.Reasons, r => r.Code == ReasonCodes.TeamBusy);
    }

    [Fact]
    public void 能力不足的队伍被排除并给出原因()
    {
        var input = SeedLikeInput() with
        {
            Tasks = [Task(TaskT1, "T1", ["water_rescue", "first_aid"], 35)]
        };
        var result = _solver.Solve(input);

        Assert.True(result.IsFeasible);
        var t1 = Assert.Single(result.Assignments);
        Assert.Equal(TeamA, t1.TeamId); // A、C 并列 30 分钟，tie-break 取 A
        Assert.Contains(t1.Reasons, r => r.Code == ReasonCodes.CapabilityMissing); // B 缺 water_rescue
    }

    [Fact]
    public void 到达时限约束_全员超时则不可分配()
    {
        var input = SeedLikeInput() with
        {
            Tasks = [Task(TaskT1, "T1", ["first_aid"], 29)] // R2 最快也要 30 分钟
        };
        var result = _solver.Solve(input);

        Assert.False(result.IsFeasible);
        var t1 = Assert.Single(result.Unassigned);
        Assert.Contains(t1.Reasons, r => r.Code == ReasonCodes.DeadlineExceeded);
    }

    [Fact]
    public void 无危险升级_执行中任务绝不抢占_即使导致无解()
    {
        var input = OnlyBCanReachT1(allowPreemption: false);
        var result = _solver.Solve(input);

        Assert.False(result.IsFeasible);
        Assert.Empty(result.Assignments);
        var t1 = Assert.Single(result.Unassigned, u => u.TaskId == TaskT1);
        Assert.Contains(t1.Reasons, r => r.Code == ReasonCodes.TeamBusy);
        Assert.DoesNotContain(result.Assignments, a => a.IsPreemption);
    }

    [Fact]
    public void 危险升级且有全能力替代队伍_允许抢占并记录原因()
    {
        var input = OnlyBCanReachT1(allowPreemption: true);
        var result = _solver.Solve(input);

        Assert.True(result.IsFeasible);
        var t1 = Assert.Single(result.Assignments, a => a.TaskId == TaskT1);
        Assert.Equal(TeamB, t1.TeamId); // 只有 B 能到达 T1
        var t2 = Assert.Single(result.Assignments, a => a.TaskId == TaskT2);
        Assert.Equal(TeamC, t2.TeamId); // T2 由全能力替代队伍 C 接手
        Assert.True(t2.IsPreemption);
        Assert.Contains(t2.Reasons, r => r.Code == ReasonCodes.PreemptionDangerEscalation);
    }

    [Fact]
    public void 危险升级但无替代队伍_仍不可抢占()
    {
        var input = OnlyBCanReachT1(allowPreemption: true) with
        {
            Teams =
            [
                Team("A", TeamA, 3.4m, "water_rescue", "first_aid"),
                Team("B", TeamB, 2.6m, "slope_patrol", "first_aid"),
                Team("C", TeamC, 3.0m, "water_rescue", "first_aid"), // C 缺 slope_patrol，无法接手 T2
            ]
        };
        var result = _solver.Solve(input);

        Assert.False(result.IsFeasible);
        Assert.Empty(result.Assignments);
        Assert.Contains(result.Unassigned, u => u.TaskId == TaskT1);
    }

    [Fact]
    public void 已完成任务不参与分配()
    {
        var input = SeedLikeInput() with
        {
            Tasks =
            [
                Task(TaskT1, "T1", ["first_aid"], 35, DispatchTaskStatus.Completed, TeamA),
                Task(TaskT2, "T2", ["slope_patrol"], null, DispatchTaskStatus.InProgress, TeamB, TaskKind.SlopeOperation),
            ]
        };
        var result = _solver.Solve(input);

        Assert.True(result.IsFeasible);
        Assert.DoesNotContain(result.Assignments, a => a.TaskId == TaskT1);   // 已完成：不分配、不计原因
        Assert.DoesNotContain(result.Unassigned, u => u.TaskId == TaskT1);
        Assert.Single(result.Assignments, a => a.TaskId == TaskT2 && a.TeamId == TeamB);
    }

    [Fact]
    public void 执行中任务不因ETA变短被移动()
    {
        // R2 恢复后 A/C 经 R2 都只需 30 分钟，但 T1 已由 C 出发执行：必须锁定，不改派也不改道
        var input = SeedLikeInput() with
        {
            Tasks =
            [
                Task(TaskT1, "T1", ["first_aid"], 35, DispatchTaskStatus.InProgress, TeamC),
                Task(TaskT2, "T2", ["slope_patrol"], null, DispatchTaskStatus.InProgress, TeamB, TaskKind.SlopeOperation),
            ]
        };
        var result = _solver.Solve(input);

        Assert.True(result.IsFeasible);
        var t1 = Assert.Single(result.Assignments, a => a.TaskId == TaskT1);
        Assert.Equal(TeamC, t1.TeamId);
        Assert.Null(t1.RoadId);          // 已到达现场，不再上路
        Assert.Equal(0, t1.EtaMinutes);
        Assert.False(t1.IsPreemption);
        Assert.Contains(t1.Reasons, r => r.Code == ReasonCodes.LockedInProgress);
        Assert.Contains(result.Assignments, a => a.TaskId == TaskT2 && a.TeamId == TeamB);
    }

    [Fact]
    public void 未标记执行的任务在道路恢复后可被重排() 
    {
        // 对照组：T1 若仍只是 Assigned（未执行），R2 恢复后允许按成本/tie-break 重排
        var input = SeedLikeInput() with
        {
            Tasks =
            [
                Task(TaskT1, "T1", ["first_aid"], 35, DispatchTaskStatus.Assigned, TeamC),
                Task(TaskT2, "T2", ["slope_patrol"], null, DispatchTaskStatus.InProgress, TeamB, TaskKind.SlopeOperation),
            ]
        };
        var result = _solver.Solve(input);

        Assert.True(result.IsFeasible);
        var t1 = Assert.Single(result.Assignments, a => a.TaskId == TaskT1);
        Assert.Equal(TeamA, t1.TeamId);   // A、C 经 R2 并列 30 分钟，tie-break 取 A
        Assert.Equal(RoadR2, t1.RoadId);
        Assert.Equal(30, t1.EtaMinutes);
    }

    /// <summary>T1 只有 B 能到（R9 限高 2.8m），T2 可由 C 经 R10 接手。</summary>
    private static SolveInput OnlyBCanReachT1(bool allowPreemption) =>
        new(
            Teams:
            [
                Team("A", TeamA, 3.4m, "water_rescue", "first_aid"),
                Team("B", TeamB, 2.6m, "slope_patrol", "first_aid"),
                Team("C", TeamC, 3.0m, "water_rescue", "first_aid", "slope_patrol"),
            ],
            Tasks:
            [
                Task(TaskT1, "T1", ["first_aid"], 10, kind: TaskKind.LifeSafety, danger: DangerLevel.Critical),
                Task(TaskT2, "T2", ["slope_patrol"], null, DispatchTaskStatus.InProgress, TeamB, TaskKind.SlopeOperation),
            ],
            Roads:
            [
                Road(Guid.Parse("00000000-0000-0000-0000-0000000000f9"), "R9", 2.8m, 8),
                Road(Guid.Parse("00000000-0000-0000-0000-0000000000f0"), "R10", 4.0m, 20),
            ],
            Options: new SolveOptions(allowPreemption, "T1 生命危险等级上升"));
}
