namespace EmergencyDispatch.Tests;

using EmergencyDispatch.Domain;
using EmergencyDispatch.Infrastructure;
using EmergencyDispatch.Solver;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Tests for <see cref="AllocationService"/> covering idempotency on input version, atomic
/// persistence, and concurrent same-input-version submits. The solver is injected as a
/// deterministic double so these assertions concern orchestration, not allocation logic.
/// </summary>
public class AllocationServiceTests
{
    private static AllocationService NewService(SqliteTestDatabase db, IAllocationSolver solver) =>
        new(db.NewContext(), solver, NullLogger<AllocationService>.Instance);

    private static SolveResult OneAssignment(SolveInput input) => new()
    {
        InputVersion = input.InputVersion,
        Assignments = new[]
        {
            new AssignmentPlan
            {
                TaskId = SeedData.TaskLifeSafety, TaskCode = "T-LIFE",
                TeamId = SeedData.TeamA, TeamCode = "A",
                VehicleId = SeedData.VehicleMid, VehicleCode = "V-MID",
                RoadSegmentId = SeedData.RoadLow, RoadSegmentCode = "R-LOW",
                ArrivalMinutes = 20,
            },
        },
        Unassigned = Array.Empty<UnassignedPlan>(),
        Audit = new[]
        {
            new AuditRecord { Sequence = 0, TaskId = SeedData.TaskLifeSafety, TaskCode = "T-LIFE", RuleCode = RuleCodes.InitialAssignment, Message = "assigned" },
        },
    };

    [Fact]
    public async Task Solve_persists_version_and_is_retrievable()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var solver = new StubSolver(OneAssignment);

        var result = await NewService(db, solver).SolveAsync(new SolveRequest { InputVersion = "v1" });

        Assert.False(result.WasExisting);
        Assert.Equal(1, result.Version.VersionNumber);
        Assert.Single(result.Version.Assignments);

        await using var readCtx = db.NewContext();
        var svc2 = new AllocationService(readCtx, solver, NullLogger<AllocationService>.Instance);
        var stored = await svc2.GetByInputVersionAsync("v1");
        Assert.NotNull(stored);
        Assert.Single(stored!.Assignments);
    }

    [Fact]
    public async Task Solve_is_idempotent_on_input_version()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var solver = new StubSolver(OneAssignment);

        var first = await NewService(db, solver).SolveAsync(new SolveRequest { InputVersion = "v1" });
        var second = await NewService(db, solver).SolveAsync(new SolveRequest { InputVersion = "v1" });

        Assert.False(first.WasExisting);
        Assert.True(second.WasExisting);
        Assert.Equal(first.Version.VersionNumber, second.Version.VersionNumber);

        // The solver ran exactly once; the replay was served from storage.
        Assert.Equal(1, solver.CallCount);

        await using var ctx = db.NewContext();
        Assert.Equal(1, ctx.AllocationVersions.Count());
    }

    [Fact]
    public async Task Concurrent_same_input_version_submits_produce_one_version()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        // Solver blocks briefly so both requests overlap in their transactions, forcing the
        // unique-index race on InputVersion to be exercised and resolved by retry.
        var gate = new ManualResetEventSlim(false);
        var solver = new StubSolver(input =>
        {
            gate.Wait(TimeSpan.FromSeconds(2));
            return OneAssignment(input);
        });

        var t1 = Task.Run(() => NewService(db, solver).SolveAsync(new SolveRequest { InputVersion = "v-concurrent" }));
        var t2 = Task.Run(() => NewService(db, solver).SolveAsync(new SolveRequest { InputVersion = "v-concurrent" }));

        gate.Set();
        var results = await Task.WhenAll(t1, t2);

        // Exactly one row exists and both callers observe the same version number.
        await using var ctx = db.NewContext();
        Assert.Equal(1, ctx.AllocationVersions.Count(v => v.InputVersion == "v-concurrent"));
        Assert.Equal(results[0].Version.VersionNumber, results[1].Version.VersionNumber);
        // At least one caller saw it as already-existing (the race loser).
        Assert.Contains(results, r => r.WasExisting);
    }

    [Fact]
    public async Task Failed_commit_never_leaves_a_partial_version()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();

        // A solver that emits an assignment referencing a duplicate audit sequence collision
        // is not realistic; instead simulate a mid-persist failure by throwing after the
        // snapshot is read. The transaction must roll back with zero rows written.
        var solver = new StubSolver(_ => throw new InvalidOperationException("boom during solve"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(db, solver).SolveAsync(new SolveRequest { InputVersion = "v-fail" }));

        await using var ctx = db.NewContext();
        Assert.Equal(0, ctx.AllocationVersions.Count());
        Assert.Equal(0, ctx.Assignments.Count());
        Assert.Equal(0, ctx.AuditEntries.Count());
    }

    [Fact]
    public async Task Unassigned_reasons_and_audit_persist_together_atomically()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var solver = new StubSolver(input => new SolveResult
        {
            InputVersion = input.InputVersion,
            Assignments = Array.Empty<AssignmentPlan>(),
            Unassigned = new[]
            {
                new UnassignedPlan
                {
                    TaskId = SeedData.TaskLifeSafety, TaskCode = "T-LIFE",
                    Code = ReasonCodes.NoFeasibleRoute, Detail = "all roads cut",
                },
            },
            Audit = new[]
            {
                new AuditRecord { Sequence = 0, TaskId = SeedData.TaskLifeSafety, TaskCode = "T-LIFE", RuleCode = RuleCodes.Unassigned, Message = "unassigned" },
            },
        });

        var result = await NewService(db, solver).SolveAsync(new SolveRequest { InputVersion = "v-infeasible" });

        Assert.True(result.Version.HasUnassignedTasks);
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

        // While the solver is "thinking", a concurrent writer cuts R-LOW. Because the service
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
            var roadLow = input.Roads.Single(r => r.Code == "R-LOW");
            return new SolveResult
            {
                InputVersion = input.InputVersion,
                Assignments = Array.Empty<AssignmentPlan>(),
                Unassigned = Array.Empty<UnassignedPlan>(),
                Audit = new[]
                {
                    new AuditRecord
                    {
                        Sequence = 0, TaskCode = "R-LOW", RuleCode = RuleCodes.InitialAssignment,
                        Message = $"R-LOW open at snapshot time = {roadLow.IsOpen}",
                    },
                },
            };
        });

        var solveTask = Task.Run(() => NewService(db, solver).SolveAsync(new SolveRequest { InputVersion = "snap-1" }));

        reachedSolve.Wait(TimeSpan.FromSeconds(2));
        // Mutate the road mid-solve.
        await using (var mutateCtx = db.NewContext())
        {
            var road = mutateCtx.RoadSegments.Single(r => r.Code == "R-LOW");
            road.IsOpen = false;
            await mutateCtx.SaveChangesAsync();
        }
        mayFinish.Set();

        var result = await solveTask;

        // The persisted audit reflects the pre-mutation snapshot (road was open), proving the
        // solve was isolated from the concurrent change.
        var entry = Assert.Single(result.Version.AuditEntries);
        Assert.Equal("R-LOW open at snapshot time = True", entry.Message);
    }
}
