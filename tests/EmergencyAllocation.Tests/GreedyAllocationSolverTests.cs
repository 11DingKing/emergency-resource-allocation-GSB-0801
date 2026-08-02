using EmergencyAllocation.Domain;
using EmergencyAllocation.Domain.Solver;
using FluentAssertions;
using Xunit;
using TaskStatus = EmergencyAllocation.Domain.TaskStatus;

namespace EmergencyAllocation.Tests;

public class GreedyAllocationSolverTests
{
    private readonly GreedyAllocationSolver _solver = new();

    [Fact]
    public void Solve_SeedScenario_AssignsLifeTaskToTeamC_KeepsBOnSlope()
    {
        var teamA = SolverTestData.Team("A", "BASE_A",
            new[] { Capabilities.WaterRescue, Capabilities.FirstAid }, 3.4, "VA",
            Seed.TeamAId, Seed.VehAId);
        var teamB = SolverTestData.Team("B", "BASE_B",
            new[] { Capabilities.SlopeInspection, Capabilities.FirstAid }, 2.6, "VB",
            Seed.TeamBId, Seed.VehBId);
        var teamC = SolverTestData.Team("C", "BASE_C",
            new[] { Capabilities.WaterRescue, Capabilities.SlopeInspection, Capabilities.FirstAid },
            3.0, "VC", Seed.TeamCId, Seed.VehCId);

        var life = SolverTestData.PendingTask("T1", "SITE_LIFE",
            TaskSeverity.LifeSafety,
            new[] { Capabilities.WaterRescue, Capabilities.FirstAid },
            45, 35, Seed.TaskLifeId);

        var slope = new SolverTask(
            Seed.TaskSlopeId, "T2", "Slope", "SITE_SLOPE",
            TaskSeverity.Urgent, 1, TaskStatus.InProgress, 60, null,
            new[] { Capabilities.SlopeInspection, Capabilities.FirstAid }.ToHashSet(),
            Seed.TeamBId, Seed.VehBId, true);

        var roads = new[]
        {
            SolverTestData.Road("R1", "BASE_A", "SITE_LIFE", 18, 3.2),
            SolverTestData.Road("R2", "BASE_C", "SITE_LIFE", 22, 4.0),
            SolverTestData.Road("RB", "BASE_B", "SITE_SLOPE", 10, null)
        };

        var result = _solver.Solve(new SolverRequest(
            new[] { teamA, teamB, teamC }, new[] { life, slope }, roads, 1,
            "initial", false, null));

        result.Feasible.Should().BeTrue();
        var lifeAssignment = result.Assignments.Single(a => a.TaskCode == "T1");
        lifeAssignment.Decision.Should().Be(AllocationDecision.Assigned);
        lifeAssignment.TeamCode.Should().Be("C");
        lifeAssignment.VehicleCode.Should().Be("VC");
        lifeAssignment.MeetsDeadline.Should().BeTrue();
        lifeAssignment.EstimatedTravelMinutes.Should().Be(22);

        var slopeAssignment = result.Assignments.Single(a => a.TaskCode == "T2");
        slopeAssignment.Decision.Should().Be(AllocationDecision.Kept);
        slopeAssignment.TeamCode.Should().Be("B");
    }

    [Fact]
    public void Solve_HeightLimitRejectsVehicleA_ExplainsViaAudit()
    {
        var teamA = SolverTestData.Team("A", "BASE_A",
            new[] { Capabilities.WaterRescue }, 3.4, "VA");
        var roads = new[] { SolverTestData.Road("R1", "BASE_A", "SITE", 10, 3.2) };
        var task = SolverTestData.PendingTask("T1", "SITE", TaskSeverity.Urgent,
            new[] { Capabilities.WaterRescue });

        var result = _solver.Solve(new SolverRequest(
            new[] { teamA }, new[] { task }, roads, 1, "initial", false, null));

        result.Feasible.Should().BeFalse();
        result.Assignments.Single().Decision.Should().Be(AllocationDecision.Unassigned);
        result.Audit.Should().Contain(a => a.Kind == "candidate-reject" && a.VehicleCode == "VA");
        result.Audit.Should().Contain(a => a.Kind == "no-feasible-solution");
    }

    [Fact]
    public void Solve_WhenR2Closed_LifeTaskUnassignedWithReason()
    {
        var teamA = SolverTestData.Team("A", "BASE_A",
            new[] { Capabilities.WaterRescue, Capabilities.FirstAid }, 3.4, "VA",
            Seed.TeamAId, Seed.VehAId);
        var teamB = SolverTestData.Team("B", "BASE_B",
            new[] { Capabilities.SlopeInspection, Capabilities.FirstAid }, 2.6, "VB",
            Seed.TeamBId, Seed.VehBId);
        var teamC = SolverTestData.Team("C", "BASE_C",
            new[] { Capabilities.WaterRescue, Capabilities.SlopeInspection, Capabilities.FirstAid },
            3.0, "VC", Seed.TeamCId, Seed.VehCId);

        var life = SolverTestData.PendingTask("T1", "SITE_LIFE",
            TaskSeverity.LifeSafety,
            new[] { Capabilities.WaterRescue, Capabilities.FirstAid }, 45, 35, Seed.TaskLifeId);
        var slope = new SolverTask(
            Seed.TaskSlopeId, "T2", "Slope", "SITE_SLOPE",
            TaskSeverity.Urgent, 1, TaskStatus.InProgress, 60, null,
            new[] { Capabilities.SlopeInspection, Capabilities.FirstAid }.ToHashSet(),
            Seed.TeamBId, Seed.VehBId, true);

        var roads = new[]
        {
            SolverTestData.Road("R1", "BASE_A", "SITE_LIFE", 18, 3.2),
            SolverTestData.Road("R2", "BASE_C", "SITE_LIFE", 22, 4.0, open: false),
            SolverTestData.Road("RB", "BASE_B", "SITE_SLOPE", 10, null)
        };

        var result = _solver.Solve(new SolverRequest(
            new[] { teamA, teamB, teamC }, new[] { life, slope }, roads, 2,
            "rearrange", true, "R2 flooded"));

        result.Feasible.Should().BeFalse();
        var lifeAssignment = result.Assignments.Single(a => a.TaskCode == "T1");
        lifeAssignment.Decision.Should().Be(AllocationDecision.Unassigned);
        lifeAssignment.Reason.Should().Contain("R2");
        result.RoadSnapshotVersion.Should().Be(2);
    }

    [Fact]
    public void Solve_TieBreakIsDeterministic_TeamCodeWins()
    {
        var teamA = SolverTestData.Team("A", "BASE",
            new[] { Capabilities.WaterRescue }, 2.5, "VA");
        var teamB = SolverTestData.Team("B", "BASE",
            new[] { Capabilities.WaterRescue }, 2.5, "VB");
        var roads = new[] { SolverTestData.Road("R1", "BASE", "SITE", 10) };
        var task = SolverTestData.PendingTask("T1", "SITE", TaskSeverity.Urgent,
            new[] { Capabilities.WaterRescue });

        var result1 = _solver.Solve(new SolverRequest(
            new[] { teamB, teamA }, new[] { task }, roads, 1, "initial", false, null));
        var result2 = _solver.Solve(new SolverRequest(
            new[] { teamA, teamB }, new[] { task }, roads, 1, "initial", false, null));

        result1.Assignments.Single().TeamCode.Should().Be("A");
        result2.Assignments.Single().TeamCode.Should().Be("A");
        result1.Assignments.Single().TeamId.Should().Be(result2.Assignments.Single().TeamId);
    }

    [Fact]
    public void Solve_DeadlineMissesCandidate_ExplainedInReason()
    {
        var team = SolverTestData.Team("A", "BASE",
            new[] { Capabilities.WaterRescue }, 2.5);
        var roads = new[] { SolverTestData.Road("R1", "BASE", "SITE", 40) };
        var task = SolverTestData.PendingTask("T1", "SITE", TaskSeverity.LifeSafety,
            new[] { Capabilities.WaterRescue }, 30, deadline: 20);

        var result = _solver.Solve(new SolverRequest(
            new[] { team }, new[] { task }, roads, 1, "initial", false, null));

        result.Feasible.Should().BeFalse();
        result.Assignments.Single().Reason.Should().Contain("deadline");
    }

    [Fact]
    public void Solve_StartedTaskCannotBePreempted_UnlessLifeSafetyAndReplacementExists()
    {
        var teamB = SolverTestData.Team("B", "BASE_B",
            new[] { Capabilities.SlopeInspection, Capabilities.FirstAid }, 2.6, "VB",
            Seed.TeamBId, Seed.VehBId);
        var teamC = SolverTestData.Team("C", "BASE_C",
            new[] { Capabilities.WaterRescue, Capabilities.SlopeInspection, Capabilities.FirstAid },
            3.0, "VC", Seed.TeamCId, Seed.VehCId);

        var slope = new SolverTask(
            Seed.TaskSlopeId, "T2", "Slope", "SITE_SLOPE",
            TaskSeverity.LifeSafety, 2, TaskStatus.InProgress, 60, null,
            new[]
            {
                Capabilities.WaterRescue,
                Capabilities.SlopeInspection,
                Capabilities.FirstAid
            }.ToHashSet(),
            Seed.TeamBId, Seed.VehBId, true);

        var roads = new[]
        {
            SolverTestData.Road("RB", "BASE_B", "SITE_SLOPE", 5),
            SolverTestData.Road("RC", "BASE_C", "SITE_SLOPE", 8, 4.0)
        };

        var result = _solver.Solve(new SolverRequest(
            new[] { teamB, teamC }, new[] { slope }, roads, 1, "rearrange",
            true, "Flash flood: water rescue required"));

        var reassigned = result.Assignments.Single(a => a.TaskCode == "T2");
        reassigned.Decision.Should().Be(AllocationDecision.Reassigned);
        reassigned.Preempted.Should().BeTrue();
        reassigned.TeamCode.Should().Be("C");
        reassigned.PreviousTeamId.Should().Be(Seed.TeamBId);
        result.Audit.Should().Contain(a => a.Kind == "task-reassigned" && a.Message.Contains("severity"));
    }

    [Fact]
    public void Solve_ReassignRejected_WhenNoFullyCapableReplacement()
    {
        var teamB = SolverTestData.Team("B", "BASE_B",
            new[] { Capabilities.SlopeInspection }, 2.6, "VB",
            Seed.TeamBId, Seed.VehBId);

        var slope = new SolverTask(
            Seed.TaskSlopeId, "T2", "Slope", "SITE_SLOPE",
            TaskSeverity.LifeSafety, 2, TaskStatus.InProgress, 60, null,
            new[] { Capabilities.WaterRescue, Capabilities.SlopeInspection }.ToHashSet(),
            Seed.TeamBId, Seed.VehBId, true);

        var roads = new[] { SolverTestData.Road("RB", "BASE_B", "SITE_SLOPE", 5) };

        var result = _solver.Solve(new SolverRequest(
            new[] { teamB }, new[] { slope }, roads, 1, "rearrange", true, "escalation"));

        result.Assignments.Single().Decision.Should().Be(AllocationDecision.Kept);
        result.Audit.Should().Contain(a => a.Kind == "reassign-failed");
    }

    [Fact]
    public void Solve_DoesNotPreemptRoutineStartedTask()
    {
        var teamA = SolverTestData.Team("A", "BASE_A",
            new[] { Capabilities.WaterRescue }, 2.6, "VA",
            Seed.TeamAId, Seed.VehAId);
        var teamC = SolverTestData.Team("C", "BASE_C",
            new[] { Capabilities.WaterRescue }, 2.6, "VC",
            Seed.TeamCId, Seed.VehCId);

        var started = new SolverTask(
            Guid.NewGuid(), "T-START", "x", "SITE", TaskSeverity.Routine, 1,
            TaskStatus.InProgress, 30, null,
            new[] { Capabilities.WaterRescue }.ToHashSet(),
            Seed.TeamAId, Seed.VehAId, true);

        var roads = new[] { SolverTestData.Road("R1", "BASE_A", "SITE", 5) };

        var result = _solver.Solve(new SolverRequest(
            new[] { teamA, teamC }, new[] { started }, roads, 1, "rearrange", true, null));

        result.Assignments.Single().Decision.Should().Be(AllocationDecision.Kept);
        result.Assignments.Single().TeamCode.Should().Be("A");
    }
}

internal static class Seed
{
    public static readonly Guid TeamAId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid TeamBId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid TeamCId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid VehAId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    public static readonly Guid VehBId = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    public static readonly Guid VehCId = Guid.Parse("cccccccc-3333-3333-3333-333333333333");
    public static readonly Guid TaskLifeId = Guid.Parse("00000000-0000-0000-0000-000000000101");
    public static readonly Guid TaskSlopeId = Guid.Parse("00000000-0000-0000-0000-000000000102");
}
