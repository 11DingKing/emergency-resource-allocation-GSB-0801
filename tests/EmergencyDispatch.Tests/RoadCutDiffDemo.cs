namespace EmergencyDispatch.Tests;

using EmergencyDispatch.Domain;
using EmergencyDispatch.Infrastructure;
using EmergencyDispatch.Solver;
using Xunit.Abstractions;

/// <summary>
/// Runs the real greedy solver against the seeded scenario before and after cutting the
/// low-clearance road, and emits the assignment diff plus the rule behind each change. This
/// both documents the scenario and guards the exact numbers referenced in the README.
/// </summary>
public class RoadCutDiffDemo
{
    private readonly ITestOutputHelper _out;

    public RoadCutDiffDemo(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Prints_before_and_after_road_cut_diff()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var solver = new GreedyAllocationSolver();

        // BEFORE the cut.
        var before = await Solve(db, solver, "demo-before", replan: false);

        // Cut R-LOW.
        await using (var ctx = db.NewContext())
        {
            var road = ctx.RoadSegments.Single(r => r.Code == "R-LOW");
            road.IsOpen = false;
            await ctx.SaveChangesAsync();
        }

        // AFTER the cut (replan permitted, though no escalation occurs here).
        var after = await Solve(db, solver, "demo-after", replan: true);

        _out.WriteLine("=== BEFORE ROAD CUT ===");
        Dump(before);
        _out.WriteLine("=== AFTER ROAD CUT (R-LOW closed) ===");
        Dump(after);

        var beforeLife = before.Assignments.Single(a => a.TaskCode == "T-LIFE");
        var afterLife = after.Assignments.Single(a => a.TaskCode == "T-LIFE");
        Assert.Equal("R-LOW", beforeLife.RoadSegmentCode);
        Assert.Equal(20, beforeLife.ArrivalMinutes);
        Assert.Equal("R-HIGH", afterLife.RoadSegmentCode);
        Assert.Equal(30, afterLife.ArrivalMinutes);
    }

    private static async Task<AllocationVersion> Solve(
        SqliteTestDatabase db, IAllocationSolver solver, string inputVersion, bool replan)
    {
        await using var ctx = db.NewContext();
        var svc = new AllocationService(ctx, solver, Microsoft.Extensions.Logging.Abstractions.NullLogger<AllocationService>.Instance);
        var result = await svc.SolveAsync(new SolveRequest { InputVersion = inputVersion, IsReplan = replan });
        return result.Version;
    }

    private void Dump(AllocationVersion v)
    {
        _out.WriteLine($"version={v.VersionNumber} kind={v.Kind} cost={v.TotalCostMinutes}min");
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
