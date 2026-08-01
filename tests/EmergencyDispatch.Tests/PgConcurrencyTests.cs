using EmergencyDispatch.Domain;
using EmergencyDispatch.Domain.Solving;
using EmergencyDispatch.Infrastructure.Persistence;
using EmergencyDispatch.Infrastructure.Seeding;
using EmergencyDispatch.Infrastructure.Services;
using EmergencyDispatch.Solver;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmergencyDispatch.Tests;

/// <summary>
/// PostgreSQL 并发专项：同一输入版本并发提交 → 唯一约束/序列化冲突 → 事务回滚重试 → 全部拿到同一方案。
/// 需要环境变量 ERA_TEST_PG 指向测试库（会自动重建）。未设置时跳过。
/// </summary>
public class PgConcurrencyTests
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

    [Fact]
    public async Task 并发同一输入版本_只生效一份方案()
    {
        var conn = Environment.GetEnvironmentVariable("ERA_TEST_PG");
        if (string.IsNullOrEmpty(conn)) return; // 无测试库时跳过

        var options = new DbContextOptionsBuilder<DispatchDbContext>().UseNpgsql(conn).Options;

        await using (var setup = new DispatchDbContext(options))
        {
            await setup.Database.EnsureDeletedAsync();
            await setup.Database.MigrateAsync();
            await DbSeeder.SeedIfEmptyAsync(setup);
        }

        var solver = new CountingSolver(new DeterministicAllocationSolver());
        const int concurrency = 8;
        var outcomes = await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
        {
            await using var db = new DispatchDbContext(options); // 每个"请求"独立作用域
            var orch = new AllocationOrchestrator(db, solver);
            return await orch.SolveAsync("concurrent-v1", PlanKind.Initial, null);
        }));

        var planIds = outcomes.Select(o => o.Plan.Id).Distinct().ToArray();
        Assert.Single(planIds);                       // 所有人拿到同一份方案
        Assert.Single(outcomes, o => !o.IsReplay);    // 只有一个真正求解并提交
        Assert.Equal(concurrency - 1, outcomes.Count(o => o.IsReplay));

        await using var verify = new DispatchDbContext(options);
        Assert.Equal(1, await verify.Plans.CountAsync(p => p.InputVersion == "concurrent-v1"));
        Assert.Equal(1, await verify.Plans.CountAsync(p => p.Status == PlanStatus.Committed));
        Assert.Equal(2, await verify.Assignments.CountAsync()); // T1 + T2，无重复插入
    }

    [Fact]
    public async Task 并发下求解中途更新道路快照_不产生半套状态()
    {
        var conn = Environment.GetEnvironmentVariable("ERA_TEST_PG");
        if (string.IsNullOrEmpty(conn)) return;

        var options = new DbContextOptionsBuilder<DispatchDbContext>().UseNpgsql(conn).Options;

        await using (var setup = new DispatchDbContext(options))
        {
            await setup.Database.EnsureDeletedAsync();
            await setup.Database.MigrateAsync();
            await DbSeeder.SeedIfEmptyAsync(setup);
        }

        // 一个请求在求解，另一个并发修改任务（触发 xmin 乐观并发 → 回滚重试）
        var solveTask = Task.Run(async () =>
        {
            await using var db = new DispatchDbContext(options);
            var orch = new AllocationOrchestrator(db, new DeterministicAllocationSolver());
            return await orch.SolveAsync("snapshot-v1", PlanKind.Initial, null);
        });

        var mutateTask = Task.Run(async () =>
        {
            await using var db = new DispatchDbContext(options);
            var t1 = await db.Tasks.SingleAsync(t => t.Code == "T1");
            t1.Danger = DangerLevel.Elevated;
            await db.SaveChangesAsync();
        });

        await Task.WhenAll(solveTask, mutateTask);
        var outcome = await solveTask;

        await using var verify = new DispatchDbContext(options);
        var committed = await verify.Plans.CountAsync(p => p.Status == PlanStatus.Committed);
        var byVersion = await verify.Plans.CountAsync(p => p.InputVersion == "snapshot-v1");
        Assert.Equal(1, committed);
        Assert.Equal(1, byVersion);
        Assert.Equal(PlanStatus.Committed, outcome.Plan.Status);
    }
}
