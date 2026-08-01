namespace EmergencyDispatch.Tests;

using EmergencyDispatch.Domain;
using EmergencyDispatch.Solver;

/// <summary>
/// Unit tests for the deterministic greedy solver. These operate purely on
/// <see cref="SolveInput"/> snapshots — no database — matching the isolation contract.
/// </summary>
public class GreedyAllocationSolverTests
{
    private static readonly Guid TeamA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TeamB = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid TeamC = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly Guid VHigh = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"); // 3.4
    private static readonly Guid VLow = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");  // 2.6
    private static readonly Guid VMid = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");  // 3.0
    private static readonly Guid TLife = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid TSlope = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid RLow = Guid.Parse("dddddddd-0000-0000-0000-000000000001");  // 3.2 limit
    private static readonly Guid RHigh = Guid.Parse("dddddddd-0000-0000-0000-000000000002"); // 4.0 limit

    private readonly GreedyAllocationSolver _solver = new();

    private static IReadOnlySet<string> Caps(params string[] c) =>
        new HashSet<string>(c, StringComparer.Ordinal);

    private static SolveInput BaseInput(
        bool roadLowOpen = true,
        bool roadHighOpen = true,
        bool isReplan = false,
        int lifeDanger = 3,
        int? lifePrevDanger = null,
        string inputVersion = "v1")
    {
        return new SolveInput
        {
            InputVersion = inputVersion,
            IsReplan = isReplan,
            Teams = new[]
            {
                new TeamSnapshot { Id = TeamA, Code = "A", Capabilities = Caps(Capabilities.WaterRescue, Capabilities.FirstAid) },
                new TeamSnapshot { Id = TeamB, Code = "B", Capabilities = Caps(Capabilities.SlopeInspection, Capabilities.FirstAid) },
                new TeamSnapshot { Id = TeamC, Code = "C", Capabilities = Caps(Capabilities.WaterRescue, Capabilities.SlopeInspection, Capabilities.FirstAid) },
            },
            Vehicles = new[]
            {
                new VehicleSnapshot { Id = VHigh, Code = "V-HIGH", HeightMeters = 3.4m },
                new VehicleSnapshot { Id = VLow, Code = "V-LOW", HeightMeters = 2.6m },
                new VehicleSnapshot { Id = VMid, Code = "V-MID", HeightMeters = 3.0m },
            },
            Tasks = new[]
            {
                new TaskSnapshot
                {
                    Id = TLife, Code = "T-LIFE",
                    RequiredCapabilities = Caps(Capabilities.WaterRescue, Capabilities.FirstAid),
                    DeadlineMinutes = 35, ServiceMinutes = 40, DangerLevel = lifeDanger,
                    IsInProgress = false, PreviousDangerLevel = lifePrevDanger,
                },
                new TaskSnapshot
                {
                    Id = TSlope, Code = "T-SLOPE",
                    RequiredCapabilities = Caps(Capabilities.SlopeInspection),
                    DeadlineMinutes = 60, ServiceMinutes = 60, DangerLevel = 1,
                    IsInProgress = true, PreviousDangerLevel = 1,
                    ExecutingTeamId = TeamB, ExecutingVehicleId = VLow,
                },
            },
            Roads = new[]
            {
                new RoadSnapshot { Id = RLow, Code = "R-LOW", HeightLimitMeters = 3.2m, IsOpen = roadLowOpen },
                new RoadSnapshot { Id = RHigh, Code = "R-HIGH", HeightLimitMeters = 4.0m, IsOpen = roadHighOpen },
            },
            Routes = new[]
            {
                new RouteSnapshot { Id = Guid.NewGuid(), TaskId = TLife, RoadSegmentId = RLow, TravelMinutes = 20 },
                new RouteSnapshot { Id = Guid.NewGuid(), TaskId = TLife, RoadSegmentId = RHigh, TravelMinutes = 30 },
                new RouteSnapshot { Id = Guid.NewGuid(), TaskId = TSlope, RoadSegmentId = RHigh, TravelMinutes = 25 },
            },
        };
    }

    [Fact]
    public void InitialSolve_assigns_life_task_and_holds_inprogress_slope()
    {
        var result = _solver.Solve(BaseInput());

        var life = Assert.Single(result.Assignments, a => a.TaskCode == "T-LIFE");
        // Fastest feasible route to T-LIFE is R-LOW at 20 min; V-LOW is held by B, so the
        // capable free team A takes it. Vehicle must fit R-LOW's 3.2m limit.
        Assert.Equal("A", life.TeamCode);
        Assert.Equal(20, life.ArrivalMinutes);
        Assert.Equal("R-LOW", life.RoadSegmentCode);

        var slope = Assert.Single(result.Assignments, a => a.TaskCode == "T-SLOPE");
        Assert.Equal("B", slope.TeamCode);
        Assert.Contains(result.Audit, a => a.RuleCode == RuleCodes.NonPreemptionHeld && a.TaskCode == "T-SLOPE");
        Assert.Empty(result.Unassigned);
    }

    [Fact]
    public void Determinism_same_input_yields_identical_result()
    {
        var a = _solver.Solve(BaseInput());
        var b = _solver.Solve(BaseInput());

        Assert.Equal(
            a.Assignments.Select(x => (x.TaskCode, x.TeamCode, x.VehicleCode, x.RoadSegmentCode, x.ArrivalMinutes)),
            b.Assignments.Select(x => (x.TaskCode, x.TeamCode, x.VehicleCode, x.RoadSegmentCode, x.ArrivalMinutes)));
        Assert.Equal(
            a.Audit.Select(x => (x.Sequence, x.RuleCode, x.TaskCode)),
            b.Audit.Select(x => (x.Sequence, x.RuleCode, x.TaskCode)));
    }

    [Fact]
    public void RoadCut_forces_reroute_and_changes_arrival()
    {
        // Cut R-LOW: only R-HIGH (30 min) reaches T-LIFE; a vehicle fitting 4.0m is fine.
        var result = _solver.Solve(BaseInput(roadLowOpen: false));

        var life = Assert.Single(result.Assignments, a => a.TaskCode == "T-LIFE");
        Assert.Equal("R-HIGH", life.RoadSegmentCode);
        Assert.Equal(30, life.ArrivalMinutes);
        Assert.True(life.ArrivalMinutes <= 35, "must still meet the 35-minute deadline");
    }

    [Fact]
    public void Infeasible_when_all_roads_cut_records_no_feasible_route()
    {
        var result = _solver.Solve(BaseInput(roadLowOpen: false, roadHighOpen: false));

        var reason = Assert.Single(result.Unassigned, u => u.TaskCode == "T-LIFE");
        Assert.Equal(ReasonCodes.NoFeasibleRoute, reason.Code);
        Assert.DoesNotContain(result.Assignments, a => a.TaskCode == "T-LIFE");
    }

    [Fact]
    public void Infeasible_when_no_capable_team()
    {
        var input = BaseInput() with
        {
            Teams = new[]
            {
                // Only a slope team exists; nobody can do water rescue.
                new TeamSnapshot { Id = TeamB, Code = "B", Capabilities = Caps(Capabilities.SlopeInspection, Capabilities.FirstAid) },
            },
        };

        var result = _solver.Solve(input);

        var reason = Assert.Single(result.Unassigned, u => u.TaskCode == "T-LIFE");
        Assert.Equal(ReasonCodes.NoCapableTeam, reason.Code);
    }

    [Fact]
    public void Deadline_exceeded_is_reported_distinctly_from_route_failure()
    {
        // Reachable, vehicle fits, but every route is slower than the deadline.
        var input = BaseInput() with
        {
            Routes = new[]
            {
                new RouteSnapshot { Id = Guid.NewGuid(), TaskId = TLife, RoadSegmentId = RLow, TravelMinutes = 50 },
                new RouteSnapshot { Id = Guid.NewGuid(), TaskId = TLife, RoadSegmentId = RHigh, TravelMinutes = 55 },
            },
        };

        var result = _solver.Solve(input);

        var reason = Assert.Single(result.Unassigned, u => u.TaskCode == "T-LIFE");
        Assert.Equal(ReasonCodes.DeadlineExceeded, reason.Code);
    }

    [Fact]
    public void EqualCost_options_use_deterministic_tiebreak_with_audit()
    {
        // Two equal-cost (20 min) routes to T-LIFE over different roads that both admit a
        // fitting vehicle. Tie-break must pick the ordinally-smallest (team, vehicle, road)
        // and record a DETERMINISTIC_TIEBREAK audit entry.
        var input = BaseInput() with
        {
            // Remove the in-progress slope task so B and its vehicle are free — creates a
            // genuine multi-candidate tie for the life task.
            Tasks = new[]
            {
                new TaskSnapshot
                {
                    Id = TLife, Code = "T-LIFE",
                    RequiredCapabilities = Caps(Capabilities.WaterRescue, Capabilities.FirstAid),
                    DeadlineMinutes = 35, ServiceMinutes = 40, DangerLevel = 3, IsInProgress = false,
                },
            },
            Routes = new[]
            {
                new RouteSnapshot { Id = Guid.NewGuid(), TaskId = TLife, RoadSegmentId = RLow, TravelMinutes = 20 },
                new RouteSnapshot { Id = Guid.NewGuid(), TaskId = TLife, RoadSegmentId = RHigh, TravelMinutes = 20 },
            },
        };

        var result = _solver.Solve(input);

        var life = Assert.Single(result.Assignments, a => a.TaskCode == "T-LIFE");
        // Team A (ordinally < C), and among equal-arrival roads R-HIGH < R-LOW ordinally,
        // vehicle V-HIGH < V-LOW < V-MID ordinally but must fit the chosen road limit.
        Assert.Equal("A", life.TeamCode);
        Assert.Contains(result.Audit, a => a.RuleCode == RuleCodes.DeterministicTieBreak && a.TaskCode == "T-LIFE");

        // Re-solving yields the identical tie-break choice.
        var again = _solver.Solve(input);
        var life2 = Assert.Single(again.Assignments, a => a.TaskCode == "T-LIFE");
        Assert.Equal((life.TeamCode, life.VehicleCode, life.RoadSegmentCode), (life2.TeamCode, life2.VehicleCode, life2.RoadSegmentCode));
    }

    [Fact]
    public void Replan_without_danger_escalation_keeps_inprogress_task_locked()
    {
        // Force contention: only team C can serve a (hypothetical) task that also needs slope,
        // but C is unavailable. Here we assert that with no danger escalation, an in-progress
        // task is never preempted even during a replan.
        var input = BaseInput(isReplan: true, lifeDanger: 3, lifePrevDanger: 3);
        var result = _solver.Solve(input);

        // B is still holding the slope task; the non-preemption audit is present.
        Assert.Contains(result.Audit, a => a.RuleCode == RuleCodes.NonPreemptionHeld && a.TaskCode == "T-SLOPE");
        Assert.DoesNotContain(result.Audit, a => a.RuleCode == RuleCodes.ReplanDangerEscalated);
    }

    [Fact]
    public void Replan_with_danger_escalation_and_capable_alternative_allows_preemption()
    {
        // The only team that can serve the escalated life task is team C, which is busy on
        // the in-progress slope task. Team B can cover the slope task (it has slope-inspection).
        // When danger escalates and a capable free alternative (B) exists, C is preempted:
        // C moves to T-LIFE and B takes over T-SLOPE.
        var input = new SolveInput
        {
            InputVersion = "replan-escalate",
            IsReplan = true,
            Teams = new[]
            {
                new TeamSnapshot { Id = TeamB, Code = "B", Capabilities = Caps(Capabilities.SlopeInspection, Capabilities.FirstAid) },
                new TeamSnapshot { Id = TeamC, Code = "C", Capabilities = Caps(Capabilities.WaterRescue, Capabilities.SlopeInspection, Capabilities.FirstAid) },
            },
            Vehicles = new[]
            {
                new VehicleSnapshot { Id = VLow, Code = "V-LOW", HeightMeters = 2.6m },
                new VehicleSnapshot { Id = VMid, Code = "V-MID", HeightMeters = 3.0m },
            },
            Tasks = new[]
            {
                new TaskSnapshot
                {
                    Id = TLife, Code = "T-LIFE",
                    RequiredCapabilities = Caps(Capabilities.WaterRescue, Capabilities.FirstAid),
                    DeadlineMinutes = 35, ServiceMinutes = 40,
                    DangerLevel = 5, PreviousDangerLevel = 2, // escalated
                    IsInProgress = false,
                },
                new TaskSnapshot
                {
                    Id = TSlope, Code = "T-SLOPE",
                    RequiredCapabilities = Caps(Capabilities.SlopeInspection),
                    DeadlineMinutes = 60, ServiceMinutes = 60, DangerLevel = 1, PreviousDangerLevel = 1,
                    IsInProgress = true, ExecutingTeamId = TeamC, ExecutingVehicleId = VLow,
                },
            },
            Roads = new[]
            {
                new RoadSnapshot { Id = RLow, Code = "R-LOW", HeightLimitMeters = 3.2m, IsOpen = true },
                new RoadSnapshot { Id = RHigh, Code = "R-HIGH", HeightLimitMeters = 4.0m, IsOpen = true },
            },
            Routes = new[]
            {
                new RouteSnapshot { Id = Guid.NewGuid(), TaskId = TLife, RoadSegmentId = RLow, TravelMinutes = 20 },
            },
        };

        var result = _solver.Solve(input);

        // C is freed from slope to serve the escalated life task; B covers the slope task.
        Assert.Contains(result.Audit, a => a.RuleCode == RuleCodes.ReplanDangerEscalated && a.TaskCode == "T-LIFE");
        Assert.Contains(result.Audit, a => a.RuleCode == RuleCodes.TeamReassigned && a.TaskCode == "T-SLOPE");
        Assert.Contains(result.Assignments, a => a.TaskCode == "T-LIFE" && a.TeamCode == "C");
        Assert.Contains(result.Assignments, a => a.TaskCode == "T-SLOPE" && a.TeamCode == "B");
    }
}
