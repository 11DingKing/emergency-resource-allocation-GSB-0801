using EmergencyAllocation.Core;
using EmergencyAllocation.Core.Entities;
using EmergencyAllocation.Core.Solving;
using EmergencyAllocation.Infrastructure.Services;
using EmergencyAllocation.Infrastructure.Solver;
using TaskStatus = EmergencyAllocation.Core.TaskStatus;

namespace EmergencyAllocation.Tests;

public class DeterministicSolverTests
{
    private readonly DeterministicSchedulingSolver _solver = new();

    [Fact]
    public async Task InitialSolve_AssignsLifeTaskToC_ThroughHeightLimitedRoad()
    {
        var problem = BuildProblem(TestSeed.Tasks(), TestSeed.Roads(), dangerRaised: false, allowPreemption: false);

        var result = await _solver.SolveAsync(problem);

        Assert.True(result.IsFeasible);
        var t1 = Assert.Single(result.Assignments, a => a.TaskId == "T1");
        Assert.Equal("C", t1.TeamId);
        Assert.Equal(new[] { TestSeed.Depot, TestSeed.Water }, t1.RouteNodes);
        Assert.Equal(20, t1.EstimatedArrivalMinutes);
        Assert.Equal(AssignmentKind.NewAssignment, t1.Kind);

        var t2 = Assert.Single(result.Assignments, a => a.TaskId == "T2");
        Assert.Equal("B", t2.TeamId);
        Assert.Equal(AssignmentKind.Kept, t2.Kind);
    }

    [Fact]
    public async Task RoadR1Closed_WithDangerRaised_PreemptsC_AndTieBreaksToA()
    {
        var tasks = TestSeed.Tasks(
            t1Status: TaskStatus.InProgress,
            t1AssignedTeam: "C",
            t2Status: TaskStatus.InProgress,
            t2AssignedTeam: "B");
        var roads = TestSeed.Roads(r1Open: false);
        var problem = BuildProblem(tasks, roads, dangerRaised: true, allowPreemption: true);

        var result = await _solver.SolveAsync(problem);

        Assert.True(result.IsFeasible);
        var t1 = Assert.Single(result.Assignments, a => a.TaskId == "T1");
        Assert.Equal("A", t1.TeamId);
        Assert.Equal(AssignmentKind.ReassignedTo, t1.Kind);
        Assert.Equal(new[] { TestSeed.Depot, TestSeed.Slope, TestSeed.Water }, t1.RouteNodes);
        Assert.Equal(35, t1.EstimatedArrivalMinutes);
        Assert.NotNull(t1.PreemptionReason);

        Assert.Contains(result.Explanations, e =>
            e.Kind == ExplanationKind.Preemption && e.RuleCode == RuleCodes.PreemptionAllowed);
        Assert.Contains(result.Explanations, e => e.RelatedTaskId == "T1" && e.RelatedTeamId == "A");
    }

    [Fact]
    public async Task RoadR1Closed_DangerNotRaised_DoesNotPreemptKeepsC()
    {
        var tasks = TestSeed.Tasks(
            t1Status: TaskStatus.InProgress,
            t1AssignedTeam: "C",
            t2Status: TaskStatus.InProgress,
            t2AssignedTeam: "B");
        var roads = TestSeed.Roads(r1Open: false);
        var problem = BuildProblem(tasks, roads, dangerRaised: false, allowPreemption: true);

        var result = await _solver.SolveAsync(problem);

        Assert.True(result.IsFeasible);
        var t1 = Assert.Single(result.Assignments, a => a.TaskId == "T1");
        Assert.Equal("C", t1.TeamId);
        Assert.Equal(AssignmentKind.Kept, t1.Kind);
        Assert.Equal(35, t1.EstimatedArrivalMinutes);
        Assert.Contains(result.Explanations, e => e.RuleCode == RuleCodes.NonPreemptive);
    }

    [Fact]
    public async Task NoFeasibleRoute_ReturnsInfeasibleWithReason()
    {
        var roads = TestSeed.Roads(r1Open: false, r3Open: false);
        var problem = BuildProblem(TestSeed.Tasks(), roads, dangerRaised: false, allowPreemption: false);

        var result = await _solver.SolveAsync(problem);

        Assert.False(result.IsFeasible);
        Assert.NotNull(result.NoFeasibleReason);
        Assert.Contains(result.Explanations, e => e.Kind == ExplanationKind.NoFeasibleSolution);
    }

    [Fact]
    public async Task CapabilityMismatch_BCannotDoWaterRescue()
    {
        var tasks = TestSeed.Tasks(t2Status: TaskStatus.Completed, t2AssignedTeam: null);
        var problem = BuildProblem(tasks, TestSeed.Roads(), dangerRaised: false, allowPreemption: false);

        var result = await _solver.SolveAsync(problem);

        var t1 = Assert.Single(result.Assignments, a => a.TaskId == "T1");
        Assert.NotEqual("B", t1.TeamId);
        Assert.Contains(result.Explanations, e =>
            e.RelatedTeamId == "B" && e.Message.Contains("water_rescue") && e.RuleCode == RuleCodes.CapabilityMatch);
    }

    [Fact]
    public async Task HeightLimit_AVehicleBlockedFromOnlyRoad()
    {
        var teamA = new Team
        {
            Id = "A", Name = "A", VehicleId = "V_A", HomeNode = TestSeed.Depot, CurrentNode = TestSeed.Depot,
            IsAvailable = true,
            Capabilities = new List<string> { Capabilities.WaterRescue, Capabilities.FirstAid },
            Vehicle = new Vehicle { Id = "V_A", HeightMeters = 3.4m }
        };
        var task = new EmergencyTask
        {
            Id = "T1", LocationNode = TestSeed.Water, RequiredArrivalMinutes = 35, DurationMinutes = 45,
            DangerLevel = DangerLevel.LifeSafety, Status = TaskStatus.Pending,
            RequiredCapabilities = new List<string> { Capabilities.WaterRescue, Capabilities.FirstAid }
        };
        var road = new RoadSegment
        {
            Id = "R1", FromNode = TestSeed.Depot, ToNode = TestSeed.Water,
            HeightLimitMeters = 3.2m, TravelTimeMinutes = 20, IsOpen = true
        };
        var problem = SnapshotBuilder.Build(new List<Team> { teamA }, new List<EmergencyTask> { task },
            new List<RoadSegment> { road },
            new SolverOptions(false, false, _solver.Version), null, "height-test");

        var result = await _solver.SolveAsync(problem);

        Assert.False(result.IsFeasible);
        Assert.Contains(result.Explanations, e =>
            e.Kind == ExplanationKind.RoadBlocked &&
            e.RelatedTeamId == "A" &&
            e.Message.Contains("3.4") &&
            e.Message.Contains("3.2"));
    }

    [Fact]
    public async Task DeterministicTieBreak_SameAcrossRepeatedSolves()
    {
        var tasks = TestSeed.Tasks(
            t1Status: TaskStatus.InProgress,
            t1AssignedTeam: "C");
        var roads = TestSeed.Roads(r1Open: false);
        var problem = BuildProblem(tasks, roads, dangerRaised: true, allowPreemption: true);

        var first = await _solver.SolveAsync(problem);
        var second = await _solver.SolveAsync(problem);
        var third = await _solver.SolveAsync(problem);

        var firstTeam = first.Assignments.Single(a => a.TaskId == "T1").TeamId;
        Assert.Equal(firstTeam, second.Assignments.Single(a => a.TaskId == "T1").TeamId);
        Assert.Equal(firstTeam, third.Assignments.Single(a => a.TaskId == "T1").TeamId);
        Assert.Equal("A", firstTeam);
        Assert.True(first.TotalCost == second.TotalCost && second.TotalCost == third.TotalCost);
    }

    [Fact]
    public async Task TwoOptimalSolutionsSameCost_DeterministicallyPicksLowerTeamId()
    {
        var teams = new List<Team>
        {
            new()
            {
                Id = "A", Name = "A", VehicleId = "V_A", HomeNode = TestSeed.Depot, CurrentNode = TestSeed.Depot,
                IsAvailable = true,
                Capabilities = new List<string> { Capabilities.WaterRescue, Capabilities.FirstAid }
            },
            new()
            {
                Id = "C", Name = "C", VehicleId = "V_C", HomeNode = TestSeed.Depot, CurrentNode = TestSeed.Depot,
                IsAvailable = true,
                Capabilities = new List<string> { Capabilities.WaterRescue, Capabilities.FirstAid }
            }
        };
        var vehicles = new List<Vehicle>
        {
            new() { Id = "V_A", HeightMeters = 3.0m },
            new() { Id = "V_C", HeightMeters = 3.0m }
        };
        var tasks = new List<EmergencyTask>
        {
            new()
            {
                Id = "T1", LocationNode = TestSeed.Water, RequiredArrivalMinutes = 35, DurationMinutes = 45,
                DangerLevel = DangerLevel.LifeSafety, Status = TaskStatus.Pending,
                RequiredCapabilities = new List<string> { Capabilities.WaterRescue, Capabilities.FirstAid }
            }
        };
        var roads = TestSeed.Roads();
        foreach (var t in teams)
        {
            t.Vehicle = vehicles.First(v => v.Id == t.VehicleId);
        }

        var problem = SnapshotBuilder.Build(teams, tasks, roads,
            new SolverOptions(false, false, _solver.Version), null, "tie-test");

        var result = await _solver.SolveAsync(problem);

        var t1 = Assert.Single(result.Assignments, a => a.TaskId == "T1");
        Assert.Equal("A", t1.TeamId);
    }

    [Fact]
    public async Task ArrivalAfterDeadline_IsInfeasible()
    {
        var tasks = TestSeed.Tasks();
        tasks[0].RequiredArrivalMinutes = 5;
        var problem = BuildProblem(tasks, TestSeed.Roads(r1Open: false), dangerRaised: false, allowPreemption: false);

        var result = await _solver.SolveAsync(problem);

        Assert.False(result.IsFeasible);
        Assert.Contains("超过", result.NoFeasibleReason!);
    }

    private static SchedulingProblem BuildProblem(
        IReadOnlyList<EmergencyTask> tasks,
        IReadOnlyList<RoadSegment> roads,
        bool dangerRaised,
        bool allowPreemption)
    {
        var teams = TestSeed.Teams();
        var vehicles = TestSeed.Vehicles;
        foreach (var team in teams)
        {
            team.Vehicle = vehicles.First(v => v.Id == team.VehicleId);
        }

        return SnapshotBuilder.Build(teams, tasks, roads,
            new SolverOptions(dangerRaised, allowPreemption, "deterministic-1.0"), null, "test-version");
    }
}
