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

    [Fact]
    public async Task RoadReopenEvent_RestoresR2AndChangesRoadHash()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        await using (var ctx = factory.CreateDbContext())
        {
            await SeedAsync(ctx);
        }

        var service = new AllocationService(factory, new DeterministicSchedulingSolver(), NullLogger<AllocationService>.Instance);
        var admin = new AdministrativeDataService(factory, NullLogger<AdministrativeDataService>.Instance);

        var initial = await service.SolveInitialAsync(new InitialSolveRequest("reopen-v0"));
        var initialRoadHash = initial.SnapshotHashes.Roads;

        await admin.RecordRoadEventAsync(new RoadEventRequest("road-r2-closed-01", false, "塌方", "dc"));
        var closed = await service.SolveInitialAsync(new InitialSolveRequest("reopen-v1-closed"));
        Assert.NotEqual(initialRoadHash, closed.SnapshotHashes.Roads);

        await admin.RecordRoadEventAsync(new RoadEventRequest("road-r2-open-02", true, "塌方清理完毕，R2恢复通行", "dc"));

        await using var verify = factory.CreateDbContext();
        Assert.True((await verify.RoadSegments.SingleAsync(r => r.Id == "R2")).IsOpen);
        var events = await verify.RoadEvents.Where(e => e.RoadSegmentId == "R2").OrderBy(e => e.OccurredAt).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.False(events[0].IsOpen);
        Assert.True(events[1].IsOpen);
        Assert.Equal("road-r2-open-02", events[1].Id);

        var reopened = await service.SolveInitialAsync(new InitialSolveRequest("reopen-v2-open"));
        Assert.Equal(initialRoadHash, reopened.SnapshotHashes.Roads);
    }

    [Fact]
    public async Task T1Completed_NotMovedWhenR2Reopens_EvenWithShorterEtaAvailable()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        await using (var ctx = factory.CreateDbContext())
        {
            await SeedAsync(ctx);
        }

        var service = new AllocationService(factory, new DeterministicSchedulingSolver(), NullLogger<AllocationService>.Instance);
        var admin = new AdministrativeDataService(factory, NullLogger<AdministrativeDataService>.Instance);

        var v0 = await service.SolveInitialAsync(new InitialSolveRequest("completed-v0"));

        await admin.RecordRoadEventAsync(new RoadEventRequest("road-r2-closed-01", false, "暴雨塌方", "dc"));
        await admin.UpdateTaskAsync("T1", new TaskStateUpdateRequest(
            Status: TaskStatus.InProgress, AssignedTeamId: "A", DangerLevel: DangerLevel.Critical));

        var v1 = await service.RearrangeAsync(new RearrangeRequest(
            "r2-critical-v1", v0.VersionId, true, TriggeringRoadEventId: "road-r2-closed-01"));
        var t1V1 = Assert.Single(v1.Assignments, a => a.TaskId == "T1");
        Assert.Equal("C", t1V1.TeamId);
        Assert.Equal("ReassignedTo", t1V1.Kind);

        await admin.RecordRoadEventAsync(new RoadEventRequest("road-r2-open-02", true, "R2恢复通行", "dc"));
        await admin.UpdateTaskAsync("T1", new TaskStateUpdateRequest(
            Status: TaskStatus.Completed, AssignedTeamId: "C", DangerLevel: DangerLevel.Critical, CurrentNode: TestSeed.Water));

        var v2 = await service.RearrangeAsync(new RearrangeRequest(
            "r2-reopened-v2", v1.VersionId, false, TriggeringRoadEventId: "road-r2-open-02"));

        Assert.True(v2.IsFeasible);
        Assert.Equal("road-r2-open-02", v2.TriggeringRoadEventId);

        var t1V2 = Assert.Single(v2.Assignments, a => a.TaskId == "T1");
        Assert.Equal("C", t1V2.TeamId);
        Assert.Equal("Kept", t1V2.Kind);
        Assert.Null(t1V2.PreemptionReason);

        var t2V2 = Assert.Single(v2.Assignments, a => a.TaskId == "T2");
        Assert.Equal("B", t2V2.TeamId);
        Assert.Equal("Kept", t2V2.Kind);

        Assert.Contains(v2.Explanations, e =>
            e.RuleCode == RuleCodes.NonPreemptive &&
            e.RelatedTaskId == "T1" &&
            e.Message.Contains("已执行完毕"));
    }

    [Fact]
    public async Task StaleExpectedHash_MissingReopenEvent_Returns409()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        await using (var ctx = factory.CreateDbContext())
        {
            await SeedAsync(ctx);
        }

        var service = new AllocationService(factory, new DeterministicSchedulingSolver(), NullLogger<AllocationService>.Instance);
        var admin = new AdministrativeDataService(factory, NullLogger<AdministrativeDataService>.Instance);

        var v0 = await service.SolveInitialAsync(new InitialSolveRequest("stale-reopen-v0"));

        await admin.RecordRoadEventAsync(new RoadEventRequest("road-r2-closed-01", false, "塌方", "dc"));
        await admin.UpdateTaskAsync("T1", new TaskStateUpdateRequest(
            Status: TaskStatus.InProgress, AssignedTeamId: "A", DangerLevel: DangerLevel.Critical));
        var v1 = await service.RearrangeAsync(new RearrangeRequest(
            "stale-r2-v1", v0.VersionId, true, TriggeringRoadEventId: "road-r2-closed-01"));

        var staleRoadHash = v1.SnapshotHashes.Roads;

        await admin.RecordRoadEventAsync(new RoadEventRequest("road-r2-open-02", true, "R2恢复", "dc"));
        await admin.UpdateTaskAsync("T1", new TaskStateUpdateRequest(
            Status: TaskStatus.Completed, AssignedTeamId: "C"));

        var ex = await Assert.ThrowsAsync<SnapshotConflictException>(() =>
            service.RearrangeAsync(new RearrangeRequest(
                "r2-reopened-v2", v1.VersionId, false,
                ExpectedRoadSnapshotHash: staleRoadHash,
                TriggeringRoadEventId: "road-r2-open-02")));

        Assert.Equal("EXPECTED_SNAPSHOT_MISMATCH", ex.Response.ConflictType);
        Assert.Contains(ex.Response.FieldDiffs, d => d.Field == "roads");
        var roadDiff = ex.Response.FieldDiffs.First(d => d.Field == "roads");
        Assert.Equal(staleRoadHash, roadDiff.ExpectedHash);
        Assert.NotEqual(staleRoadHash, roadDiff.ActualHash);
    }

    [Fact]
    public async Task SameVersionOldSnapshotAfterReopen_Returns409WithEventDiff()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        await using (var ctx = factory.CreateDbContext())
        {
            await SeedAsync(ctx);
        }

        var service = new AllocationService(factory, new DeterministicSchedulingSolver(), NullLogger<AllocationService>.Instance);
        var admin = new AdministrativeDataService(factory, NullLogger<AdministrativeDataService>.Instance);

        var v0 = await service.SolveInitialAsync(new InitialSolveRequest("old-snap-v0"));

        await admin.RecordRoadEventAsync(new RoadEventRequest("road-r2-closed-01", false, "塌方", "dc"));
        await admin.UpdateTaskAsync("T1", new TaskStateUpdateRequest(
            Status: TaskStatus.InProgress, AssignedTeamId: "A", DangerLevel: DangerLevel.Critical));
        var v1 = await service.RearrangeAsync(new RearrangeRequest(
            "old-snap-v1", v0.VersionId, true, TriggeringRoadEventId: "road-r2-closed-01"));

        await admin.RecordRoadEventAsync(new RoadEventRequest("road-r2-open-02", true, "R2恢复", "dc"));
        await admin.UpdateTaskAsync("T1", new TaskStateUpdateRequest(
            Status: TaskStatus.Completed, AssignedTeamId: "C"));

        var correct = await service.RearrangeAsync(new RearrangeRequest(
            "r2-reopened-v2", v1.VersionId, false, TriggeringRoadEventId: "road-r2-open-02"));
        Assert.True(correct.IsFeasible);

        await admin.UpdateTaskAsync("T2", new TaskStateUpdateRequest(
            Status: TaskStatus.Completed, AssignedTeamId: "B"));
        var ex = await Assert.ThrowsAsync<SnapshotConflictException>(() =>
            service.RearrangeAsync(new RearrangeRequest(
                "r2-reopened-v2", correct.VersionId, false)));

        Assert.Equal("INPUT_VERSION_SNAPSHOT_CONFLICT", ex.Response.ConflictType);
        Assert.NotNull(ex.Response.CommittedHashes);
        Assert.NotEqual(ex.Response.CommittedHashes!.Combined, ex.Response.CurrentHashes.Combined);
        Assert.Contains(ex.Response.FieldDiffs, d => d.Field == "tasks");
    }

    [Fact]
    public async Task Round2ToRound3_DiffIsTraceable_ContainsAssignmentAndSnapshotChanges()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        await using (var ctx = factory.CreateDbContext())
        {
            await SeedAsync(ctx);
        }

        var service = new AllocationService(factory, new DeterministicSchedulingSolver(), NullLogger<AllocationService>.Instance);
        var admin = new AdministrativeDataService(factory, NullLogger<AdministrativeDataService>.Instance);

        var v0 = await service.SolveInitialAsync(new InitialSolveRequest("diff-v0"));

        await admin.RecordRoadEventAsync(new RoadEventRequest("road-r2-closed-01", false, "暴雨塌方", "dc"));
        await admin.UpdateTaskAsync("T1", new TaskStateUpdateRequest(
            Status: TaskStatus.InProgress, AssignedTeamId: "A", DangerLevel: DangerLevel.Critical));
        var v1 = await service.RearrangeAsync(new RearrangeRequest(
            "r2-critical-v1", v0.VersionId, true, TriggeringRoadEventId: "road-r2-closed-01"));

        await admin.RecordRoadEventAsync(new RoadEventRequest("road-r2-open-02", true, "塌方清理完毕", "dc"));
        await admin.UpdateTaskAsync("T1", new TaskStateUpdateRequest(
            Status: TaskStatus.Completed, AssignedTeamId: "C", DangerLevel: DangerLevel.Critical, CurrentNode: TestSeed.Water));
        var v2 = await service.RearrangeAsync(new RearrangeRequest(
            "r2-reopened-v2", v1.VersionId, false, TriggeringRoadEventId: "road-r2-open-02"));

        var diff = await service.GetDiffAsync(v2.VersionId, v1.VersionId, CancellationToken.None);
        Assert.NotNull(diff);

        Assert.Equal(v1.VersionId, diff!.FromVersionId);
        Assert.Equal("r2-critical-v1", diff.FromInputVersion);
        Assert.Equal(v2.VersionId, diff.ToVersionId);
        Assert.Equal("r2-reopened-v2", diff.ToInputVersion);
        Assert.Equal("road-r2-open-02", diff.TriggeringRoadEventId);

        var roadChange = Assert.Single(diff.SnapshotChanges, s => s.Field == "roads");
        Assert.True(roadChange.Changed);
        Assert.NotEqual(roadChange.FromHash, roadChange.ToHash);

        var taskChange = Assert.Single(diff.SnapshotChanges, s => s.Field == "tasks");
        Assert.True(taskChange.Changed);

        var t1Change = diff.AssignmentChanges.FirstOrDefault(c => c.TaskId == "T1");
        Assert.NotNull(t1Change);
        Assert.Equal("C", t1Change!.FromTeamId);
        Assert.Equal("C", t1Change.ToTeamId);

        Assert.Contains(diff.ChangeReasons, r => r.Contains("road-r2-open-02"));
        Assert.Contains(diff.ChangeReasons, r => r.Contains("道路快照发生变化"));
        Assert.Contains(diff.ChangeReasons, r => r.Contains("任务快照发生变化"));
    }

    [Fact]
    public async Task CommittedVersion_PointsToWinningSnapshot_OnlyOneWinner()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        await using (var ctx = factory.CreateDbContext())
        {
            await SeedAsync(ctx);
        }

        var service = new AllocationService(factory, new DeterministicSchedulingSolver(), NullLogger<AllocationService>.Instance);
        var admin = new AdministrativeDataService(factory, NullLogger<AdministrativeDataService>.Instance);

        var v0 = await service.SolveInitialAsync(new InitialSolveRequest("winner-v0"));

        await admin.RecordRoadEventAsync(new RoadEventRequest("road-r2-closed-01", false, "塌方", "dc"));
        await admin.UpdateTaskAsync("T1", new TaskStateUpdateRequest(
            Status: TaskStatus.InProgress, AssignedTeamId: "A", DangerLevel: DangerLevel.Critical));
        var v1 = await service.RearrangeAsync(new RearrangeRequest(
            "winner-v1", v0.VersionId, true, TriggeringRoadEventId: "road-r2-closed-01"));

        await admin.RecordRoadEventAsync(new RoadEventRequest("road-r2-open-02", true, "R2恢复", "dc"));
        await admin.UpdateTaskAsync("T1", new TaskStateUpdateRequest(
            Status: TaskStatus.Completed, AssignedTeamId: "C"));

        var winner = await service.RearrangeAsync(new RearrangeRequest(
            "r2-reopened-v2", v1.VersionId, false, TriggeringRoadEventId: "road-r2-open-02"));

        var latest = await service.GetLatestCommittedAsync(CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(winner.VersionId, latest!.VersionId);
        Assert.Equal(winner.SnapshotHash, latest.SnapshotHash);
        Assert.Equal(winner.SnapshotHashes.Combined, latest.SnapshotHashes.Combined);

        var byInput = await service.GetByInputVersionAsync("r2-reopened-v2", CancellationToken.None);
        Assert.NotNull(byInput);
        Assert.Equal(winner.VersionId, byInput!.VersionId);
        Assert.Equal(winner.SnapshotHashes.Roads, byInput.SnapshotHashes.Roads);
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
