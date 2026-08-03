namespace EmergencyDispatch.Tests;

using EmergencyDispatch.Domain;
using EmergencyDispatch.Infrastructure;
using EmergencyDispatch.Solver;
using Xunit.Abstractions;

/// <summary>
/// Runs the real greedy solver through the user scenario: record road event
/// <c>road-r2-closed-01</c> closing R2, escalate T1 to <c>critical</c>, then replan as
/// <c>r2-critical-v1</c>. Emits the before/after assignment diff plus the rule behind each
/// change, and guards the exact numbers and audit codes referenced in the README.
/// </summary>
public class RoadCutDiffDemo
{
    private readonly ITestOutputHelper _out;

    public RoadCutDiffDemo(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Prints_before_and_after_road_event_diff()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var solver = new GreedyAllocationSolver();

        // BEFORE: solve against the initial snapshot.
        var snapBefore = await SnapshotVersionAsync(db);
        var before = await Solve(db, solver, "before-r2", snapBefore, replan: false);

        // Record road event road-r2-closed-01 closing R2, then escalate T1 to critical.
        await Service(db, solver).RecordRoadEventAsync("road-r2-closed-01", "R2", closed: true);
        await Service(db, solver).SetTaskDangerAsync("T1", DangerLevels.Critical);

        // AFTER: replan against the new snapshot as r2-critical-v1.
        var snapAfter = await SnapshotVersionAsync(db);
        var after = await Solve(db, solver, "r2-critical-v1", snapAfter, replan: true);

        _out.WriteLine("=== BEFORE (snapshot " + snapBefore + ") ===");
        Dump(before);
        _out.WriteLine("=== AFTER road-r2-closed-01 + T1=critical (snapshot " + snapAfter + ") ===");
        Dump(after);

        var beforeT1 = before.Assignments.Single(a => a.TaskCode == "T1");
        var afterT1 = after.Assignments.Single(a => a.TaskCode == "T1");
        Assert.Equal("R2", beforeT1.RoadSegmentCode);
        Assert.Equal(20, beforeT1.ArrivalMinutes);
        Assert.Equal("R1", afterT1.RoadSegmentCode);
        Assert.Equal(30, afterT1.ArrivalMinutes);

        // The reroute audit cites the road event; T2 stays held (non-preemption).
        Assert.Contains(after.AuditEntries, e =>
            e.RuleCode == RuleCodes.RerouteAfterRoadCut && e.TaskCode == "T1" && e.Message.Contains("road-r2-closed-01"));
        Assert.Contains(after.AuditEntries, e =>
            e.RuleCode == RuleCodes.NonPreemptionHeld && e.TaskCode == "T2");
    }

    private static AllocationService Service(SqliteTestDatabase db, IAllocationSolver solver) =>
        new(db.NewContext(), solver, Microsoft.Extensions.Logging.Abstractions.NullLogger<AllocationService>.Instance);

    private static async Task<string> SnapshotVersionAsync(SqliteTestDatabase db)
    {
        await using var ctx = db.NewContext();
        var snap = await new SnapshotService(ctx).BuildAsync();
        return snap.Version;
    }

    private static async Task<AllocationVersion> Solve(
        SqliteTestDatabase db, IAllocationSolver solver, string inputVersion, string snapshotVersion, bool replan)
    {
        var result = await Service(db, solver).SolveAsync(
            new SolveRequest { InputVersion = inputVersion, SnapshotVersion = snapshotVersion, IsReplan = replan });
        Assert.False(result.IsConflict);
        return result.Version!;
    }

    private void Dump(AllocationVersion v)
    {
        _out.WriteLine($"version={v.VersionNumber} kind={v.Kind} cost={v.TotalCostMinutes}min snapshot={v.SnapshotVersion}");
        foreach (var a in v.Assignments.OrderBy(a => a.TaskCode, StringComparer.Ordinal))
        {
            _out.WriteLine($"  ASSIGN {a.TaskCode} -> team {a.TeamCode}, vehicle {a.VehicleCode}, road {a.RoadSegmentCode}, arrive {a.ArrivalMinutes}min");
        }
        foreach (var u in v.UnassignedReasons)
        {
            _out.WriteLine($"  UNASSIGNED {u.TaskCode} [{u.Code}] {u.Detail}");
        }
        foreach (var e in v.AuditEntries.OrderBy(e => e.Sequence))
        {
            _out.WriteLine($"  AUDIT #{e.Sequence} [{e.RuleCode}] {e.TaskCode}: {e.Message}");
        }
    }
}
