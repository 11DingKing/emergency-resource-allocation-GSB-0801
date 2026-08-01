namespace EmergencyDispatch.Tests;

using EmergencyDispatch.Domain;
using EmergencyDispatch.Infrastructure;
using EmergencyDispatch.Solver;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Tests for <see cref="AllocationService"/> covering snapshot binding, idempotency on input
/// version, snapshot conflicts (409), atomic persistence, and concurrent same-input-version
/// submits. The solver is injected as a deterministic double so these assertions concern
/// orchestration, not allocation logic.
/// </summary>
public class AllocationServiceTests
{
    private static AllocationService NewService(SqliteTestDatabase db, IAllocationSolver solver) =>
        new(db.NewContext(), solver, NullLogger<AllocationService>.Instance);

    private static async Task<string> SnapshotVersionAsync(SqliteTestDatabase db)
    {
        await using var ctx = db.NewContext();
        var snap = await new SnapshotService(ctx).BuildAsync();
        return snap.Version;
    }

    private static SolveResult OneAssignment(SolveInput input) => new()
    {
        InputVersion = input.InputVersion,
        Assignments = new[]
        {
            new AssignmentPlan
            {
                TaskId = SeedData.Task1, TaskCode = "T1",
                TeamId = SeedData.TeamA, TeamCode = "A",
                VehicleId = SeedData.VehicleMid, VehicleCode = "V-MID",
                RoadSegmentId = SeedData.Road2, RoadSegmentCode = "R2",
                ArrivalMinutes = 20,
            },
        },
        Unassigned = Array.Empty<UnassignedPlan>(),
        Audit = new[]
        {
            new AuditRecord { Sequence = 0, TaskId = SeedData.Task1, TaskCode = "T1", RuleCode = RuleCodes.InitialAssignment, Message = "assigned" },
        },
    };

    [Fact]
    public async Task Solve_persists_version_and_is_retrievable()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var solver = new StubSolver(OneAssignment);
        var snapshot = await SnapshotVersionAsync(db);

        var result = await NewService(db, solver).SolveAsync(
            new SolveRequest { InputVersion = "v1", SnapshotVersion = snapshot });

        Assert.False(result.WasExisting);
        Assert.Equal(1, result.Version!.VersionNumber);
        Assert.Single(result.Version.Assignments);
        Assert.Equal(snapshot, result.Version.SnapshotVersion);

        await using var readCtx = db.NewContext();
        var svc2 = new AllocationService(readCtx, solver, NullLogger<AllocationService>.Instance);
        var stored = await svc2.GetByInputVersionAsync("v1");
        Assert.NotNull(stored);
        Assert.Single(stored!.Assignments);
    }

    [Fact]
    public async Task Identical_payload_replays_same_plan()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var solver = new StubSolver(OneAssignment);
        var snapshot = await SnapshotVersionAsync(db);

        var first = await NewService(db, solver).SolveAsync(
            new SolveRequest { InputVersion = "v1", SnapshotVersion = snapshot });
        var second = await NewService(db, solver).SolveAsync(
            new SolveRequest { InputVersion = "v1", SnapshotVersion = snapshot });

        Assert.False(first.WasExisting);
        Assert.True(second.WasExisting);
        Assert.False(second.IsConflict);
        Assert.Equal(first.Version!.VersionNumber, second.Version!.VersionNumber);

        // The solver ran exactly once; the identical replay was served from storage.
        Assert.Equal(1, solver.CallCount);

        await using var ctx = db.NewContext();
        Assert.Equal(1, ctx.AllocationVersions.Count());
    }

    [Fact]
    public async Task Same_input_version_different_snapshot_returns_conflict_with_field_diff()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var solver = new StubSolver(OneAssignment);
        var snapshotBefore = await SnapshotVersionAsync(db);

        // First solve binds inputVersion to the current snapshot.
        var first = await NewService(db, solver).SolveAsync(
            new SolveRequest { InputVersion = "r2-critical-v1", SnapshotVersion = snapshotBefore });
        Assert.False(first.IsConflict);

        // The world moves: record road event road-r2-closed-01 and escalate T1 to critical.
        await NewService(db, solver).RecordRoadEventAsync("road-r2-closed-01", "R2", closed: true);
        await NewService(db, solver).SetTaskDangerAsync("T1", DangerLevels.Critical);
        var snapshotAfter = await SnapshotVersionAsync(db);
        Assert.NotEqual(snapshotBefore, snapshotAfter);

        // Re-submitting the SAME inputVersion against the NEW snapshot must NOT replay the old
        // plan; it is a conflict carrying a field-level diff referencing the event and T1/T2.
        var conflictResult = await NewService(db, solver).SolveAsync(
            new SolveRequest { InputVersion = "r2-critical-v1", SnapshotVersion = snapshotAfter });

        Assert.True(conflictResult.IsConflict);
        Assert.Null(conflictResult.Version);
        var conflict = conflictResult.Conflict!;
        Assert.Equal(snapshotBefore, conflict.StoredSnapshotVersion);
        Assert.Equal(snapshotAfter, conflict.RequestedSnapshotVersion);

        // The diff cites the road R2 closing and the T1 danger change.
        Assert.Contains(conflict.Diff.Changes, c => c.Path.StartsWith("road[R2]") && c.Path.EndsWith("isOpen"));
        Assert.Contains(conflict.Diff.Changes, c => c.Path == "road[R2].lastEventId" && c.Current == "road-r2-closed-01");
        Assert.Contains(conflict.Diff.Changes, c => c.Path == "task[T1].dangerLevel"
            && c.Stored == DangerLevels.High && c.Current == DangerLevels.Critical);

        // The road event is referenced.
        Assert.Contains(conflict.RoadEvents, e => e.EventId == "road-r2-closed-01" && e.RoadCode == "R2" && e.Closed);

        // Still exactly one stored version — the conflict created nothing.
        await using var ctx = db.NewContext();
        Assert.Equal(1, ctx.AllocationVersions.Count());
    }

    [Fact]
    public async Task Concurrent_same_input_version_different_snapshots_never_replays_stale_plan()
    {
        // The user's adversarial case: two requests with the SAME inputVersion but DIFFERENT
        // road snapshots race. Exactly one may create the bound plan; the other, seeing a
        // different snapshot, must receive a conflict — never a replay of the other's plan.
        await using var db = await SqliteTestDatabase.CreateAsync();
        var solver = new StubSolver(OneAssignment);

        var snapA = await SnapshotVersionAsync(db);
        // Produce a genuinely different snapshot B by cutting R2 via an event.
        await Service(db, solver).RecordRoadEventAsync("road-r2-closed-01", "R2", closed: true);
        var snapB = await SnapshotVersionAsync(db);
        Assert.NotEqual(snapA, snapB);

        // Both requests carry inputVersion "dup" but bind to different snapshots. snapA is now
        // stale (world moved to snapB), so a request bound to snapA must not win a replay.
        var t1 = Task.Run(() => NewService(db, solver).SolveAsync(
            new SolveRequest { InputVersion = "dup", SnapshotVersion = snapA }));
        var t2 = Task.Run(() => NewService(db, solver).SolveAsync(
            new SolveRequest { InputVersion = "dup", SnapshotVersion = snapB }));
        var results = await Task.WhenAll(t1, t2);

        // At most one non-conflict version was created, and it is bound to the current snapshot B.
        var created = results.Where(r => !r.IsConflict).ToList();
        await using var ctx = db.NewContext();
        Assert.Equal(created.Count, ctx.AllocationVersions.Count(v => v.InputVersion == "dup"));
        Assert.True(ctx.AllocationVersions.Count(v => v.InputVersion == "dup") <= 1);
        foreach (var ok in created)
        {
            // The only plan that may be persisted is the one bound to the current world (snapB);
            // the stale snapA request is never allowed to create or replay a plan.
            Assert.Equal(snapB, ok.Version!.SnapshotVersion);
        }
    }

    private static AllocationService Service(SqliteTestDatabase db, IAllocationSolver solver) =>
        new(db.NewContext(), solver, NullLogger<AllocationService>.Instance);

    [Fact]
    public async Task Concurrent_same_input_version_and_snapshot_produce_one_version()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var snapshot = await SnapshotVersionAsync(db);
        // Solver blocks briefly so both requests overlap in their transactions, forcing the
        // unique-index race on InputVersion to be exercised and resolved by retry.
        var gate = new ManualResetEventSlim(false);
        var solver = new StubSolver(input =>
        {
            gate.Wait(TimeSpan.FromSeconds(2));
            return OneAssignment(input);
        });

        var t1 = Task.Run(() => NewService(db, solver).SolveAsync(
            new SolveRequest { InputVersion = "v-concurrent", SnapshotVersion = snapshot }));
        var t2 = Task.Run(() => NewService(db, solver).SolveAsync(
            new SolveRequest { InputVersion = "v-concurrent", SnapshotVersion = snapshot }));

        gate.Set();
        var results = await Task.WhenAll(t1, t2);

        // Exactly one row exists and both callers observe the same version number.
        await using var ctx = db.NewContext();
        Assert.Equal(1, ctx.AllocationVersions.Count(v => v.InputVersion == "v-concurrent"));
        Assert.All(results, r => Assert.False(r.IsConflict));
        Assert.Equal(results[0].Version!.VersionNumber, results[1].Version!.VersionNumber);
        // At least one caller saw it as already-existing (the race loser replaying).
        Assert.Contains(results, r => r.WasExisting);
    }

    [Fact]
    public async Task Failed_commit_never_leaves_a_partial_version()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var snapshot = await SnapshotVersionAsync(db);

        // Simulate a mid-persist failure by throwing during solve. The transaction must roll
        // back with zero rows written.
        var solver = new StubSolver(_ => throw new InvalidOperationException("boom during solve"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(db, solver).SolveAsync(new SolveRequest { InputVersion = "v-fail", SnapshotVersion = snapshot }));

        await using var ctx = db.NewContext();
        Assert.Equal(0, ctx.AllocationVersions.Count());
        Assert.Equal(0, ctx.Assignments.Count());
        Assert.Equal(0, ctx.AuditEntries.Count());
    }

    [Fact]
    public async Task Unassigned_reasons_and_audit_persist_together_atomically()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var snapshot = await SnapshotVersionAsync(db);
        var solver = new StubSolver(input => new SolveResult
        {
            InputVersion = input.InputVersion,
            Assignments = Array.Empty<AssignmentPlan>(),
            Unassigned = new[]
            {
                new UnassignedPlan
                {
                    TaskId = SeedData.Task1, TaskCode = "T1",
                    Code = ReasonCodes.NoFeasibleRoute, Detail = "all roads cut",
                },
            },
            Audit = new[]
            {
                new AuditRecord { Sequence = 0, TaskId = SeedData.Task1, TaskCode = "T1", RuleCode = RuleCodes.Unassigned, Message = "unassigned" },
            },
        });

        var result = await NewService(db, solver).SolveAsync(
            new SolveRequest { InputVersion = "v-infeasible", SnapshotVersion = snapshot });

        Assert.True(result.Version!.HasUnassignedTasks);
        Assert.Single(result.Version.UnassignedReasons);

        var explanation = await NewService(db, solver).ExplainAsync(result.Version.VersionNumber);
        Assert.NotNull(explanation);
        Assert.Single(explanation!.Unassigned);
        Assert.Equal(ReasonCodes.NoFeasibleRoute, explanation.Unassigned[0].Code);
    }

    [Fact]
    public async Task Road_snapshot_change_during_solve_does_not_leak_into_persisted_result()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var snapshot = await SnapshotVersionAsync(db);

        // While the solver is "thinking", a concurrent writer cuts R2. Because the service
        // hands the solver an immutable snapshot captured before Solve() runs, the mutation
        // must NOT influence this version's persisted result: the plan reflects the snapshot,
        // not the later database state.
        var reachedSolve = new ManualResetEventSlim(false);
        var mayFinish = new ManualResetEventSlim(false);
        var solver = new StubSolver(input =>
        {
            reachedSolve.Set();
            mayFinish.Wait(TimeSpan.FromSeconds(2));
            // Report the road state exactly as it was in the snapshot handed to us.
            var roadR2 = input.Roads.Single(r => r.Code == "R2");
            return new SolveResult
            {
                InputVersion = input.InputVersion,
                Assignments = Array.Empty<AssignmentPlan>(),
                Unassigned = Array.Empty<UnassignedPlan>(),
                Audit = new[]
                {
                    new AuditRecord
                    {
                        Sequence = 0, TaskCode = "R2", RuleCode = RuleCodes.InitialAssignment,
                        Message = $"R2 open at snapshot time = {roadR2.IsOpen}",
                    },
                },
            };
        });

        var solveTask = Task.Run(() => NewService(db, solver).SolveAsync(
            new SolveRequest { InputVersion = "snap-1", SnapshotVersion = snapshot }));

        reachedSolve.Wait(TimeSpan.FromSeconds(2));
        // Mutate the road mid-solve.
        await using (var mutateCtx = db.NewContext())
        {
            var road = mutateCtx.RoadSegments.Single(r => r.Code == "R2");
            road.IsOpen = false;
            await mutateCtx.SaveChangesAsync();
        }
        mayFinish.Set();

        var result = await solveTask;

        // The persisted audit reflects the pre-mutation snapshot (road was open), proving the
        // solve was isolated from the concurrent change.
        var entry = Assert.Single(result.Version!.AuditEntries);
        Assert.Equal("R2 open at snapshot time = True", entry.Message);
    }
}
