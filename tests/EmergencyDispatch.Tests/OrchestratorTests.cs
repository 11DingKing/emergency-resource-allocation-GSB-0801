using EmergencyDispatch.Domain;
using EmergencyDispatch.Domain.Solving;
using EmergencyDispatch.Infrastructure.Persistence;
using EmergencyDispatch.Infrastructure.Seeding;
using EmergencyDispatch.Infrastructure.Services;
using EmergencyDispatch.Solver;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmergencyDispatch.Tests;

/// <summary>编排层测试：快照绑定、幂等回放、同版本不同快照 409、过期快照 409、Infeasible 不生效。</summary>
public class OrchestratorTests
{
    private sealed class CountingSolver(IAllocationSolver inner) : IAllocationSolver
    {
        public int Calls;
        public SolveResult Solve(SolveInput input)
        {
            Interlocked.Increment(ref Calls);
            return inner.Solve(input);
        }
    }

    private static async Task<(DispatchDbContext Db, AllocationOrchestrator Orch, CountingSolver Solver, WorldSnapshotService Snapshots)> NewSeededAsync()
    {
        var options = new DbContextOptionsBuilder<DispatchDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new DispatchDbContext(options);
        await DbSeeder.SeedIfEmptyAsync(db);
        var solver = new CountingSolver(new DeterministicAllocationSolver());
        var snapshots = new WorldSnapshotService(db);
        return (db, new AllocationOrchestrator(db, solver, snapshots), solver, snapshots);
    }

    [Fact]
    public async Task 完全相同请求_幂等回放_求解器只调用一次()
    {
        var (_, orch, solver, snapshots) = await NewSeededAsync();
        var s1 = await snapshots.CaptureAsync();

        var first = await orch.SolveAsync("v1", PlanKind.Initial, null, s1.Id);
        var second = await orch.SolveAsync("v1", PlanKind.Initial, null, s1.Id);

        Assert.False(first.IsReplay);
        Assert.True(second.IsReplay);
        Assert.Equal(first.Plan.Id, second.Plan.Id);
        Assert.Equal(1, solver.Calls);
        Assert.Equal(s1.Id, first.Plan.WorldSnapshotId);
    }

    [Fact]
    public async Task 同一输入版本配不同快照_409并给出字段级差异()
    {
        var (db, orch, _, snapshots) = await NewSeededAsync();
        var s1 = await snapshots.CaptureAsync();
        await orch.SolveAsync("v1", PlanKind.Initial, null, s1.Id);

        // 世界变化：R2 中断（等价于事件 road-r2-closed-01 生效）
        var r2 = await db.RoadSegments.SingleAsync(r => r.Code == "R2");
        r2.IsBlocked = true;
        r2.LastEventId = "road-r2-closed-01";
        await db.SaveChangesAsync();
        var s2 = await snapshots.CaptureAsync();
        Assert.NotEqual(s1.Id, s2.Id);

        var ex = await Assert.ThrowsAsync<SolveConflictException>(() =>
            orch.SolveAsync("v1", PlanKind.Replan, "换个快照重发旧版本", s2.Id));

        Assert.Equal("input_version_snapshot_conflict", ex.Conflict);
        Assert.NotNull(ex.ExistingPlanId);
        var r2Diff = Assert.Single(ex.Differences, d => d.EntityType == "road" && d.Code == "R2" && d.Field == "isBlocked");
        Assert.Equal("false", r2Diff.From);
        Assert.Equal("true", r2Diff.To);
        Assert.Equal("road-r2-closed-01", r2Diff.EventId);
    }

    [Fact]
    public async Task 引用过期快照求解_409过期冲突()
    {
        var (db, orch, _, snapshots) = await NewSeededAsync();
        var s1 = await snapshots.CaptureAsync();

        var r2 = await db.RoadSegments.SingleAsync(r => r.Code == "R2");
        r2.IsBlocked = true;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<SolveConflictException>(() =>
            orch.SolveAsync("v9", PlanKind.Initial, null, s1.Id));

        Assert.Equal("stale_snapshot", ex.Conflict);
        Assert.Contains(ex.Differences, d => d.EntityType == "road" && d.Code == "R2" && d.Field == "isBlocked");
    }

    [Fact]
    public async Task 世界状态未变_快照按内容摘要去重()
    {
        var (_, _, _, snapshots) = await NewSeededAsync();
        var a = await snapshots.CaptureAsync();
        var b = await snapshots.CaptureAsync();
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.WorldDigest, b.WorldDigest);
    }

    [Fact]
    public async Task 无可行解_方案仅审计落库_绝不生效_回放结果一致()
    {
        var (db, orch, solver, snapshots) = await NewSeededAsync();
        foreach (var r in db.RoadSegments) r.IsBlocked = true;
        await db.SaveChangesAsync();
        var s1 = await snapshots.CaptureAsync();

        var failed = await orch.SolveAsync("bad-v1", PlanKind.Initial, null, s1.Id);

        Assert.False(failed.IsReplay);
        Assert.Equal(PlanStatus.Infeasible, failed.Plan.Status);
        Assert.Empty(failed.Plan.Assignments);
        Assert.NotEmpty(failed.Plan.Unassigned);
        Assert.Null(await orch.LoadCurrentAsync(default));
        Assert.Equal(s1.Id, failed.Plan.WorldSnapshotId);

        var t1 = await db.Tasks.SingleAsync(t => t.Code == "T1");
        Assert.Null(t1.CurrentTeamId);
        Assert.Equal(DispatchTaskStatus.Pending, t1.Status);

        var replay = await orch.SolveAsync("bad-v1", PlanKind.Initial, null, s1.Id);
        Assert.True(replay.IsReplay);
        Assert.Equal(failed.Plan.Id, replay.Plan.Id);
        Assert.Equal(1, solver.Calls);
    }

    [Fact]
    public async Task 重排后旧版本被取代_当前版本唯一()
    {
        var (db, orch, _, snapshots) = await NewSeededAsync();
        var s1 = await snapshots.CaptureAsync();
        var v1 = await orch.SolveAsync("v1", PlanKind.Initial, null, s1.Id);

        var r2 = await db.RoadSegments.SingleAsync(r => r.Code == "R2");
        r2.IsBlocked = true;
        await db.SaveChangesAsync();
        var s2 = await snapshots.CaptureAsync();
        var v2 = await orch.SolveAsync("v2", PlanKind.Replan, "R2 山岭高架中断", s2.Id);

        Assert.Equal(PlanStatus.Committed, v2.Plan.Status);
        Assert.Equal(v1.Plan.Id, v2.Plan.SupersedesPlanId);
        Assert.Equal(PlanStatus.Superseded, (await orch.LoadPlanByIdAsync(v1.Plan.Id, default))!.Status);
        Assert.Equal(v2.Plan.Id, (await orch.LoadCurrentAsync(default))!.Id);
        Assert.Single(db.Plans, p => p.Status == PlanStatus.Committed);
    }

    [Fact]
    public async Task 已存在生效方案时_初始求解被拒绝()
    {
        var (_, orch, _, snapshots) = await NewSeededAsync();
        var s1 = await snapshots.CaptureAsync();
        await orch.SolveAsync("v1", PlanKind.Initial, null, s1.Id);

        var ex = await Assert.ThrowsAsync<SolveConflictException>(() => orch.SolveAsync("v2", PlanKind.Initial, null, s1.Id));
        Assert.Equal("initial_already_committed", ex.Conflict);
    }
}
