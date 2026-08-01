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
/// PostgreSQL 并发专项：
/// 1) 同一 inputVersion + 同一快照并发 → 唯一约束/序列化冲突 → 回滚重试 → 全部回放同一方案；
/// 2) 同一 inputVersion + 不同快照并发 → 恰一个成功，另一个 409（字段级差异），绝不伪装成功。
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

    private static async Task<DbContextOptions<DispatchDbContext>?> SetupAsync()
    {
        var conn = Environment.GetEnvironmentVariable("ERA_TEST_PG");
        if (string.IsNullOrEmpty(conn)) return null;
        var options = new DbContextOptionsBuilder<DispatchDbContext>().UseNpgsql(conn).Options;
        await using var setup = new DispatchDbContext(options);
        await setup.Database.EnsureDeletedAsync();
        await setup.Database.MigrateAsync();
        await DbSeeder.SeedIfEmptyAsync(setup);
        return options;
    }

    [Fact]
    public async Task 并发同一输入版本同一快照_只生效一份方案()
    {
        var options = await SetupAsync();
        if (options is null) return;

        Guid snapshotId;
        await using (var db = new DispatchDbContext(options))
            snapshotId = (await new WorldSnapshotService(db).CaptureAsync()).Id;

        var solver = new CountingSolver(new DeterministicAllocationSolver());
        const int concurrency = 8;
        var outcomes = await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
        {
            await using var db = new DispatchDbContext(options); // 每个"请求"独立作用域
            var orch = new AllocationOrchestrator(db, solver, new WorldSnapshotService(db));
            return await orch.SolveAsync("concurrent-v1", PlanKind.Initial, null, snapshotId);
        }));

        var planIds = outcomes.Select(o => o.Plan.Id).Distinct().ToArray();
        Assert.Single(planIds);
        Assert.Single(outcomes, o => !o.IsReplay);
        Assert.Equal(concurrency - 1, outcomes.Count(o => o.IsReplay));

        await using var verify = new DispatchDbContext(options);
        Assert.Equal(1, await verify.Plans.CountAsync(p => p.InputVersion == "concurrent-v1"));
        Assert.Equal(1, await verify.Plans.CountAsync(p => p.Status == PlanStatus.Committed));
        Assert.Equal(2, await verify.Assignments.CountAsync());
    }

    [Fact]
    public async Task 并发同一输入版本不同快照_一个成功一个409()
    {
        var options = await SetupAsync();
        if (options is null) return;

        Guid oldSnapshotId, newSnapshotId;
        await using (var db = new DispatchDbContext(options))
        {
            var snapshots = new WorldSnapshotService(db);
            oldSnapshotId = (await snapshots.CaptureAsync()).Id; // 关闭前

            var r2 = await db.RoadSegments.SingleAsync(r => r.Code == "R2");
            r2.IsBlocked = true;
            r2.LastEventId = "road-r2-closed-01";
            await db.SaveChangesAsync();
            newSnapshotId = (await snapshots.CaptureAsync()).Id; // 关闭后
        }

        // 并发：一个引用关闭后的新快照，一个引用关闭前的旧快照，inputVersion 相同
        var results = await Task.WhenAll(
            Solve("race-v1", newSnapshotId),
            Solve("race-v1", oldSnapshotId));

        var successes = results.Where(r => r.Outcome is not null).ToArray();
        var conflicts = results.Where(r => r.Conflict is not null).ToArray();

        Assert.Single(successes);
        Assert.Single(conflicts);
        Assert.Equal(newSnapshotId, successes[0].Outcome!.Plan.WorldSnapshotId);
        Assert.False(successes[0].Outcome!.IsReplay);
        // 视竞态时序：旧快照在写入前被过期检查拦截（stale_snapshot），或在回放检查中被绑定冲突拦截（input_version_snapshot_conflict）
        Assert.Contains(conflicts[0].Conflict!.Conflict, new[] { "stale_snapshot", "input_version_snapshot_conflict" });
        Assert.Contains(conflicts[0].Conflict!.Differences,
            d => d.EntityType == "road" && d.Code == "R2" && d.Field == "isBlocked");

        await using var verify = new DispatchDbContext(options);
        Assert.Equal(1, await verify.Plans.CountAsync(p => p.InputVersion == "race-v1"));

        async Task<(SolveOutcome? Outcome, SolveConflictException? Conflict)> Solve(string version, Guid snapshotId)
        {
            try
            {
                await using var db = new DispatchDbContext(options);
                var orch = new AllocationOrchestrator(db, new DeterministicAllocationSolver(), new WorldSnapshotService(db));
                return (await orch.SolveAsync(version, PlanKind.Initial, null, snapshotId), null);
            }
            catch (SolveConflictException ex)
            {
                return (null, ex);
            }
        }
    }

    [Fact]
    public async Task 求解前快照已过期_409且必须换新快照()
    {
        var options = await SetupAsync();
        if (options is null) return;

        Guid oldSnapshotId, newSnapshotId;
        await using (var db = new DispatchDbContext(options))
        {
            var snapshots = new WorldSnapshotService(db);
            oldSnapshotId = (await snapshots.CaptureAsync()).Id;

            var t1 = await db.Tasks.SingleAsync(t => t.Code == "T1");
            t1.Danger = DangerLevel.Critical;
            await db.SaveChangesAsync();
            newSnapshotId = (await snapshots.CaptureAsync()).Id;
        }

        await using (var db = new DispatchDbContext(options))
        {
            var orch = new AllocationOrchestrator(db, new DeterministicAllocationSolver(), new WorldSnapshotService(db));
            var ex = await Assert.ThrowsAsync<SolveConflictException>(() =>
                orch.SolveAsync("stale-v1", PlanKind.Initial, null, oldSnapshotId));
            Assert.Equal("stale_snapshot", ex.Conflict);
            Assert.Contains(ex.Differences, d => d.EntityType == "task" && d.Code == "T1" && d.Field == "danger");
        }

        await using (var db = new DispatchDbContext(options))
        {
            var orch = new AllocationOrchestrator(db, new DeterministicAllocationSolver(), new WorldSnapshotService(db));
            var outcome = await orch.SolveAsync("stale-v1", PlanKind.Initial, null, newSnapshotId);
            Assert.Equal(PlanStatus.Committed, outcome.Plan.Status);
        }
    }
}
