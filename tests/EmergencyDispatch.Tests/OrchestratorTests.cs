using EmergencyDispatch.Domain;
using EmergencyDispatch.Domain.Solving;
using EmergencyDispatch.Infrastructure.Persistence;
using EmergencyDispatch.Infrastructure.Seeding;
using EmergencyDispatch.Infrastructure.Services;
using EmergencyDispatch.Solver;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmergencyDispatch.Tests;

/// <summary>编排层测试：幂等回放、Infeasible 不生效、版本取代、半套分配不可见。求解器注入确定性实现。</summary>
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

    private static DispatchDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DispatchDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DispatchDbContext(options);
    }

    private static async Task<(DispatchDbContext Db, AllocationOrchestrator Orch, CountingSolver Solver)> NewSeededAsync()
    {
        var db = NewDb();
        await DbSeeder.SeedIfEmptyAsync(db);
        var solver = new CountingSolver(new DeterministicAllocationSolver());
        return (db, new AllocationOrchestrator(db, solver), solver);
    }

    [Fact]
    public async Task 同一输入版本重复提交_幂等回放_求解器只调用一次()
    {
        var (db, orch, solver) = await NewSeededAsync();

        var first = await orch.SolveAsync("v1", PlanKind.Initial, null);
        var second = await orch.SolveAsync("v1", PlanKind.Initial, null);

        Assert.False(first.IsReplay);
        Assert.True(second.IsReplay);
        Assert.Equal(first.Plan.Id, second.Plan.Id);
        Assert.Equal(1, solver.Calls);
        Assert.Equal(PlanStatus.Committed, second.Plan.Status);
    }

    [Fact]
    public async Task 无可行解_方案仅审计落库_绝不生效_回放结果一致()
    {
        var (db, orch, solver) = await NewSeededAsync();
        foreach (var r in db.RoadSegments) r.IsBlocked = true;
        await db.SaveChangesAsync();

        var failed = await orch.SolveAsync("bad-v1", PlanKind.Initial, null);

        Assert.False(failed.IsReplay);
        Assert.Equal(PlanStatus.Infeasible, failed.Plan.Status);
        Assert.Empty(failed.Plan.Assignments);          // 无半套分配
        Assert.NotEmpty(failed.Plan.Unassigned);
        Assert.Null(await orch.LoadCurrentAsync(default)); // 对外无生效方案

        // T1 仍未被分配、T2 仍由 B 执行：世界状态未被污染
        var t1 = await db.Tasks.SingleAsync(t => t.Code == "T1");
        var t2 = await db.Tasks.SingleAsync(t => t.Code == "T2");
        Assert.Null(t1.CurrentTeamId);
        Assert.Equal(DispatchTaskStatus.Pending, t1.Status);
        Assert.Equal(DispatchTaskStatus.InProgress, t2.Status);

        var replay = await orch.SolveAsync("bad-v1", PlanKind.Initial, null);
        Assert.True(replay.IsReplay);
        Assert.Equal(failed.Plan.Id, replay.Plan.Id);
        Assert.Equal(1, solver.Calls);
    }

    [Fact]
    public async Task 重排后旧版本被取代_当前版本唯一()
    {
        var (db, orch, _) = await NewSeededAsync();

        var v1 = await orch.SolveAsync("v1", PlanKind.Initial, null);
        var r2 = await db.RoadSegments.SingleAsync(r => r.Code == "R2");
        r2.IsBlocked = true;
        await db.SaveChangesAsync();

        var v2 = await orch.SolveAsync("v2", PlanKind.Replan, "R2 山岭高架中断");

        Assert.Equal(PlanStatus.Committed, v2.Plan.Status);
        Assert.Equal(v1.Plan.Id, v2.Plan.SupersedesPlanId);

        var reloadedV1 = await orch.LoadPlanByIdAsync(v1.Plan.Id, default);
        Assert.Equal(PlanStatus.Superseded, reloadedV1!.Status);

        var current = await orch.LoadCurrentAsync(default);
        Assert.Equal(v2.Plan.Id, current!.Id);
        Assert.Single(db.Plans, p => p.Status == PlanStatus.Committed);

        // T1 由 A 改派 C
        var t1 = v2.Plan.Assignments.Single(a => a.Task!.Code == "T1");
        Assert.Equal("C", t1.Team!.Code);
        Assert.True(t1.Road!.IsBlocked == false);
    }

    [Fact]
    public async Task 已存在生效方案时_初始求解被拒绝()
    {
        var (_, orch, _) = await NewSeededAsync();
        await orch.SolveAsync("v1", PlanKind.Initial, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => orch.SolveAsync("v2", PlanKind.Initial, null));
    }

    [Fact]
    public async Task 不同输入版本的重排各自形成新版本()
    {
        var (db, orch, _) = await NewSeededAsync();
        await orch.SolveAsync("v1", PlanKind.Initial, null);
        await orch.SolveAsync("v2", PlanKind.Replan, "例行重排");

        Assert.Equal(2, await db.Plans.CountAsync());
        Assert.Equal(2, (await orch.LoadCurrentAsync(default))!.PlanVersion);
    }
}
