using EmergencyAllocation.Core;
using EmergencyAllocation.Core.Entities;
using EmergencyAllocation.Core.Solving;
using EmergencyAllocation.Infrastructure.Persistence;
using EmergencyAllocation.Infrastructure.Services;
using EmergencyAllocation.Infrastructure.Services.Contracts;
using EmergencyAllocation.Infrastructure.Solver;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TaskStatus = EmergencyAllocation.Core.TaskStatus;

namespace EmergencyAllocation.Tests;

public class SnapshotAndCriticalTests
{
    private static InMemoryAllocationDbContextFactory NewFactory(string name) => new(name);

    private static async Task SeedAsync(AllocationDbContext context)
    {
        context.Vehicles.AddRange(TestSeed.Vehicles);
        context.Teams.AddRange(TestSeed.Teams());
        context.Tasks.AddRange(TestSeed.Tasks());
        context.RoadSegments.AddRange(TestSeed.Roads());
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task RecordRoadEvent_ClosesRoadAndAppearsInSnapshot()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        await using (var ctx = factory.CreateDbContext())
        {
            await SeedAsync(ctx);
        }

        var admin = new AdministrativeDataService(factory, NullLogger<AdministrativeDataService>.Instance);
        var evt = await admin.RecordRoadEventAsync(new RoadEventRequest(
            "road-r2-closed-01", false, "暴雨导致边坡塌方，R2中断", "调度中心"));

        Assert.Equal("R2", evt.RoadSegmentId);
        Assert.False(evt.IsOpen);

        await using var verify = factory.CreateDbContext();
        Assert.False((await verify.RoadSegments.SingleAsync(r => r.Id == "R2")).IsOpen);
        Assert.Single(await verify.RoadEvents.ToListAsync());
    }

    [Fact]
    public async Task SameInputVersion_DifferentSnapshot_Returns409WithFieldDiff()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        await using (var ctx = factory.CreateDbContext())
        {
            await SeedAsync(ctx);
        }

        var service = new AllocationService(factory, new DeterministicSchedulingSolver(), NullLogger<AllocationService>.Instance);
        var admin = new AdministrativeDataService(factory, NullLogger<AdministrativeDataService>.Instance);

        var first = await service.SolveInitialAsync(new InitialSolveRequest("conflict-v1"));
        Assert.True(first.IsFeasible);

        await admin.RecordRoadEventAsync(new RoadEventRequest("road-r2-closed-01", false, "塌方", "dc"));

        var ex = await Assert.ThrowsAsync<SnapshotConflictException>(() =>
            service.SolveInitialAsync(new InitialSolveRequest("conflict-v1")));

        Assert.Equal("INPUT_VERSION_SNAPSHOT_CONFLICT", ex.Response.ConflictType);
        Assert.Contains(ex.Response.FieldDiffs, d => d.Field == "roads");
        Assert.Contains(ex.Response.FieldDiffs, d => d.RelatedEntities.Contains("road-r2-closed-01"));
        Assert.NotNull(ex.Response.CommittedHashes);
        Assert.NotEqual(ex.Response.CommittedHashes!.Combined, ex.Response.CurrentHashes.Combined);
    }

    [Fact]
    public async Task SameInputVersion_IdenticalSnapshot_ReplaysSameResult()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        await using (var ctx = factory.CreateDbContext())
        {
            await SeedAsync(ctx);
        }

        var service = new AllocationService(factory, new DeterministicSchedulingSolver(), NullLogger<AllocationService>.Instance);

        var first = await service.SolveInitialAsync(new InitialSolveRequest("replay-v1"));
        var second = await service.SolveInitialAsync(new InitialSolveRequest("replay-v1"));

        Assert.Equal(first.VersionId, second.VersionId);
        Assert.Equal(first.SnapshotHash, second.SnapshotHash);
    }

    [Fact]
    public async Task ExpectedSnapshotHash_Mismatch_Returns409()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        await using (var ctx = factory.CreateDbContext())
        {
            await SeedAsync(ctx);
        }

        var service = new AllocationService(factory, new DeterministicSchedulingSolver(), NullLogger<AllocationService>.Instance);

        var ex = await Assert.ThrowsAsync<SnapshotConflictException>(() =>
            service.SolveInitialAsync(new InitialSolveRequest(
                "stale-v1",
                ExpectedRoadSnapshotHash: "DEADBEEFCAFEBABE")));

        Assert.Equal("EXPECTED_SNAPSHOT_MISMATCH", ex.Response.ConflictType);
        Assert.Contains(ex.Response.FieldDiffs, d => d.Field == "roads");
        Assert.Equal("DEADBEEFCAFEBABE", ex.Response.FieldDiffs[0].ExpectedHash);
    }

    [Fact]
    public async Task R2Closed_T1Critical_RearrangeAssignsC_KeepsB_ReferencesEventAndT1T2()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        await using (var ctx = factory.CreateDbContext())
        {
            await SeedAsync(ctx);
        }

        var service = new AllocationService(factory, new DeterministicSchedulingSolver(), NullLogger<AllocationService>.Instance);
        var admin = new AdministrativeDataService(factory, NullLogger<AdministrativeDataService>.Instance);

        var before = await service.SolveInitialAsync(new InitialSolveRequest("r2-critical-v0"));
        var t1Before = Assert.Single(before.Assignments, a => a.TaskId == "T1");
        Assert.Equal("C", t1Before.TeamId);

        await admin.RecordRoadEventAsync(new RoadEventRequest("road-r2-closed-01", false, "暴雨塌方，R2中断", "调度中心"));
        await admin.UpdateTaskAsync("T1", new TaskStateUpdateRequest(
            Status: TaskStatus.InProgress, AssignedTeamId: "A", DangerLevel: DangerLevel.Critical));

        var after = await service.RearrangeAsync(new RearrangeRequest(
            "r2-critical-v1", before.VersionId, true, TriggeringRoadEventId: "road-r2-closed-01"));

        Assert.True(after.IsFeasible);
        Assert.Equal("road-r2-closed-01", after.TriggeringRoadEventId);

        var t1 = Assert.Single(after.Assignments, a => a.TaskId == "T1");
        Assert.Equal("C", t1.TeamId);
        Assert.Equal("ReassignedTo", t1.Kind);
        Assert.Equal(new[] { "DEPOT", "WATER" }, t1.RouteNodes);
        Assert.Equal(20, t1.EstimatedArrivalMinutes);
        Assert.NotNull(t1.PreemptionReason);

        var t2 = Assert.Single(after.Assignments, a => a.TaskId == "T2");
        Assert.Equal("B", t2.TeamId);
        Assert.Equal("Kept", t2.Kind);

        Assert.Contains(after.Explanations, e => e.RuleCode == RuleCodes.RoadClosed && e.Message.Contains("road-r2-closed-01"));
        Assert.Contains(after.Explanations, e => e.RuleCode == RuleCodes.RoadClosed && e.Message.Contains("T1"));
        Assert.Contains(after.Explanations, e => e.RuleCode == RuleCodes.RoadClosed && e.Message.Contains("T2"));
        Assert.Contains(after.Explanations, e => e.RuleCode == RuleCodes.PreemptionAllowed && e.RelatedTaskId == "T1");
    }

    [Fact]
    public async Task T2_OnlyMovesUnderRound1PreemptionConditions()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        await using (var ctx = factory.CreateDbContext())
        {
            await SeedAsync(ctx);
        }

        var service = new AllocationService(factory, new DeterministicSchedulingSolver(), NullLogger<AllocationService>.Instance);
        var admin = new AdministrativeDataService(factory, NullLogger<AdministrativeDataService>.Instance);

        await service.SolveInitialAsync(new InitialSolveRequest("t2-v0"));
        await admin.UpdateTaskAsync("T2", new TaskStateUpdateRequest(Status: TaskStatus.InProgress, AssignedTeamId: "B"));

        var noRaise = await service.RearrangeAsync(new RearrangeRequest("t2-v1", Guid.NewGuid(), false));
        var t2NoRaise = Assert.Single(noRaise.Assignments, a => a.TaskId == "T2");
        Assert.Equal("B", t2NoRaise.TeamId);
        Assert.Equal("Kept", t2NoRaise.Kind);
        Assert.Contains(noRaise.Explanations, e => e.RuleCode == RuleCodes.NonPreemptive && e.RelatedTaskId == "T2");

        await admin.RecordRoadEventAsync(new RoadEventRequest("road-r1-closed-t2", false, "测试", "dc"));
        var raised = await service.RearrangeAsync(new RearrangeRequest("t2-v2", noRaise.VersionId, true));
        var t2Raised = Assert.Single(raised.Assignments, a => a.TaskId == "T2");
        Assert.Equal("B", t2Raised.TeamId);
        Assert.Equal("Kept", t2Raised.Kind);
    }

    [Fact]
    public async Task ConcurrentSameInputVersionDifferentSnapshots_OnlyOneCommitsLoserGets409()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        await using (var ctx = factory.CreateDbContext())
        {
            await SeedAsync(ctx);
        }

        var signaling = new SignalingSolver(new DeterministicSchedulingSolver());
        var service = new AllocationService(factory, signaling, NullLogger<AllocationService>.Instance);
        var admin = new AdministrativeDataService(factory, NullLogger<AdministrativeDataService>.Instance);

        var first = Task.Run(() => service.SolveInitialAsync(new InitialSolveRequest("race-v1")));

        await signaling.Captured;
        await admin.RecordRoadEventAsync(new RoadEventRequest("road-r2-closed-race", false, "赛道路中断", "dc"));
        signaling.Release();

        var winner = await first;
        Assert.True(winner.IsFeasible);

        var ex = await Assert.ThrowsAsync<SnapshotConflictException>(() =>
            service.SolveInitialAsync(new InitialSolveRequest("race-v1")));
        Assert.Equal("INPUT_VERSION_SNAPSHOT_CONFLICT", ex.Response.ConflictType);
    }

    private sealed class SignalingSolver : ISchedulingSolver
    {
        private readonly ISchedulingSolver _inner;
        private readonly TaskCompletionSource _captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _proceed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public SignalingSolver(ISchedulingSolver inner) => _inner = inner;
        public Task Captured => _captured.Task;
        public void Release() => _proceed.TrySetResult();
        public string Version => _inner.Version;

        public async Task<SolverResult> SolveAsync(SchedulingProblem problem, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                _captured.TrySetResult();
                await _proceed.Task;
            }

            return await _inner.SolveAsync(problem, cancellationToken);
        }
    }
}
