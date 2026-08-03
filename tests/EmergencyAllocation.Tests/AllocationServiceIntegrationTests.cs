using EmergencyAllocation.Domain;
using EmergencyAllocation.Domain.Dtos;
using EmergencyAllocation.Domain.Services;
using EmergencyAllocation.Domain.Solver;
using EmergencyAllocation.Infrastructure.Persistence;
using EmergencyAllocation.Infrastructure.Services;
using EmergencyAllocation.Tests.TestInfra;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using TaskStatus = EmergencyAllocation.Domain.TaskStatus;

namespace EmergencyAllocation.Tests;

public class AllocationServiceIntegrationTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly AllocationDbContext _db;

    public AllocationServiceIntegrationTests()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<AllocationDbContext>(o =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString())
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddSingleton<IAllocationSolver, GreedyAllocationSolver>();
        services.AddSingleton<ISnapshotDigestService, SnapshotDigestService>();
        services.AddScoped<IAllocationService, AllocationService>();
        services.AddLogging();
        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<IDbContextFactory<AllocationDbContext>>().CreateDbContext();
        Seed(_db);
    }

    private static void Seed(AllocationDbContext ctx)
    {
        var a = new Team
        {
            Id = TestSeed.TeamAId, Code = "A", Name = "A", BaseNodeId = "BASE_A",
            Capabilities =
            {
                new() { Id = Guid.NewGuid(), Capability = Capabilities.WaterRescue },
                new() { Id = Guid.NewGuid(), Capability = Capabilities.FirstAid }
            },
            Vehicles =
            {
                new Vehicle
                {
                    Id = TestSeed.VehAId, Code = "VA", HeightMeters = 3.4, TeamId = TestSeed.TeamAId
                }
            }
        };
        var b = new Team
        {
            Id = TestSeed.TeamBId, Code = "B", Name = "B", BaseNodeId = "BASE_B",
            Capabilities =
            {
                new() { Id = Guid.NewGuid(), Capability = Capabilities.SlopeInspection },
                new() { Id = Guid.NewGuid(), Capability = Capabilities.FirstAid }
            },
            Vehicles =
            {
                new Vehicle
                {
                    Id = TestSeed.VehBId, Code = "VB", HeightMeters = 2.6, TeamId = TestSeed.TeamBId
                }
            }
        };
        var c = new Team
        {
            Id = TestSeed.TeamCId, Code = "C", Name = "C", BaseNodeId = "BASE_C",
            Capabilities =
            {
                new() { Id = Guid.NewGuid(), Capability = Capabilities.WaterRescue },
                new() { Id = Guid.NewGuid(), Capability = Capabilities.SlopeInspection },
                new() { Id = Guid.NewGuid(), Capability = Capabilities.FirstAid }
            },
            Vehicles =
            {
                new Vehicle
                {
                    Id = TestSeed.VehCId, Code = "VC", HeightMeters = 3.0, TeamId = TestSeed.TeamCId
                }
            }
        };
        ctx.Teams.AddRange(a, b, c);

        ctx.Tasks.AddRange(
            new EmergencyTask
            {
                Id = TestSeed.TaskLifeId, Code = "T1", Title = "Life",
                LocationNodeId = "SITE_LIFE", Severity = TaskSeverity.LifeSafety,
                Status = TaskStatus.Pending, DurationMinutes = 45, DeadlineMinutes = 35,
                RequiredCapabilities =
                {
                    new() { Id = Guid.NewGuid(), Capability = Capabilities.WaterRescue },
                    new() { Id = Guid.NewGuid(), Capability = Capabilities.FirstAid }
                }
            },
            new EmergencyTask
            {
                Id = TestSeed.TaskSlopeId, Code = "T2", Title = "Slope",
                LocationNodeId = "SITE_SLOPE", Severity = TaskSeverity.Urgent,
                Status = TaskStatus.InProgress, DurationMinutes = 60,
                AssignedTeamId = TestSeed.TeamBId, AssignedVehicleId = TestSeed.VehBId,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                RequiredCapabilities =
                {
                    new() { Id = Guid.NewGuid(), Capability = Capabilities.SlopeInspection },
                    new() { Id = Guid.NewGuid(), Capability = Capabilities.FirstAid }
                }
            });

        ctx.RoadSegments.AddRange(
            new RoadSegment
            {
                Id = TestSeed.RoadR1Id, Code = "R1", FromNodeId = "BASE_A", ToNodeId = "SITE_LIFE",
                TravelTimeMinutes = 18, HeightLimitMeters = 3.2, IsOpen = true, RoadSnapshotVersion = 1
            },
            new RoadSegment
            {
                Id = TestSeed.RoadR2Id, Code = "R2", FromNodeId = "BASE_C", ToNodeId = "SITE_LIFE",
                TravelTimeMinutes = 22, HeightLimitMeters = 4.0, IsOpen = true, RoadSnapshotVersion = 1
            },
            new RoadSegment
            {
                Id = Guid.NewGuid(), Code = "RB", FromNodeId = "BASE_B", ToNodeId = "SITE_SLOPE",
                TravelTimeMinutes = 10, HeightLimitMeters = null, IsOpen = true, RoadSnapshotVersion = 1
            },
            new RoadSegment
            {
                Id = Guid.NewGuid(), Code = "RC", FromNodeId = "BASE_C", ToNodeId = "SITE_SLOPE",
                TravelTimeMinutes = 14, HeightLimitMeters = 4.0, IsOpen = true, RoadSnapshotVersion = 1
            });
        ctx.SaveChanges();
    }

    [Fact]
    public async Task SolveInitial_IsIdempotent_SameInputVersionReturnsSameVersion()
    {
        var svc = _provider.GetRequiredService<IAllocationService>();
        var first = await svc.SolveInitialAsync(new SolveRequestDto("v1", null, null));
        var second = await svc.SolveInitialAsync(new SolveRequestDto("v1", null, null));

        first.Id.Should().Be(second.Id);
        second.Status.Should().Be("Committed");
        second.Assignments.Should().Contain(a => a.TaskCode == "T1" && a.TeamCode == "C");
    }

    [Fact]
    public async Task RoadInterruptThenRearrange_ProducesUnassignedLifeTask_AndAuditConsistent()
    {
        var svc = _provider.GetRequiredService<IAllocationService>();

        await svc.InterruptRoadAsync(new RoadInterruptRequestDto("R2", "Flooded", "road-v2"));

        var after = await svc.RearrangeAsync(new SolveRequestDto("v-after-road", "R2 flooded", null));
        after.Status.Should().Be("NoFeasibleSolution");
        after.RoadSnapshotVersion.Should().Be(2);

        var life = after.Assignments.Single(a => a.TaskCode == "T1");
        life.Decision.Should().Be("Unassigned");
        life.TeamCode.Should().BeNull();
        life.Reason.Should().NotBeNullOrEmpty();
        life.Reason.Should().Contain("R2");

        after.Audit.Should().Contain(a => a.Kind == "no-feasible-solution");
        after.Audit.Should().Contain(a => a.Kind == "candidate-reject" && a.VehicleCode == "VA");
        after.UnassignedCount.Should().Be(1);
    }

    [Fact]
    public async Task SameInputVersion_ConcurrentSubmissions_OnlyOneVersionCommitted()
    {
        var svc = _provider.GetRequiredService<IAllocationService>();
        var request = new SolveRequestDto("concurrent-v1", null, null);

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => svc.SolveInitialAsync(request)));
        var results = await Task.WhenAll(tasks);

        results.Select(r => r.Id).Distinct().Should().HaveCount(1);
        results.First().Status.Should().Be("Committed");

        var factory = _provider.GetRequiredService<IDbContextFactory<AllocationDbContext>>();
        await using var ctx = await factory.CreateDbContextAsync();
        var committed = await ctx.AllocationVersions
            .CountAsync(v => v.Operation == "initial" && v.IdempotencyKey == "initial:concurrent-v1");
        committed.Should().Be(1);
    }

    [Fact]
    public async Task AuditEntries_AreConsistentWithAssignments()
    {
        var svc = _provider.GetRequiredService<IAllocationService>();
        var result = await svc.SolveInitialAsync(new SolveRequestDto("audit-v1", null, null));

        result.Audit.Should().Contain(a => a.Kind == "solve-start");
        result.Audit.Should().Contain(a => a.Kind == "task-assigned" && a.TaskCode == "T1");
        result.Audit.Should().Contain(a => a.Kind == "candidate-reject" && a.VehicleCode == "VA");

        var lifeAssignment = result.Assignments.Single(a => a.TaskCode == "T1");
        lifeAssignment.RouteNodeIds.Should().ContainInOrder("BASE_C", "SITE_LIFE");
        lifeAssignment.EstimatedTravelMinutes.Should().Be(22);
        lifeAssignment.MeetsDeadline.Should().BeTrue();
    }

    [Fact]
    public async Task NoHalfAllocationVisible_WhenSolverThrows_VersionIsFailedButNoTaskMutations()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<AllocationDbContext>(o =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString())
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddSingleton<IAllocationSolver>(new ThrowingSolver());
        services.AddSingleton<ISnapshotDigestService, SnapshotDigestService>();
        services.AddScoped<IAllocationService, AllocationService>();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<AllocationDbContext>>();
        using (var ctx = factory.CreateDbContext()) Seed(ctx);

        var svc = sp.GetRequiredService<IAllocationService>();
        var result = await svc.SolveInitialAsync(new SolveRequestDto("fail-v1", null, null));

        result.Status.Should().Be("Failed");
        result.FailureReason.Should().Be("simulated solver failure");

        await using var verify = await factory.CreateDbContextAsync();
        var life = await verify.Tasks.SingleAsync(t => t.Code == "T1");
        life.AssignedTeamId.Should().BeNull();
        life.Status.Should().Be(TaskStatus.Pending);
    }

    [Fact]
    public async Task SameInputVersion_AfterRoadSnapshotChange_Returns409WithFieldDiff()
    {
        var svc = _provider.GetRequiredService<IAllocationService>();
        var first = await svc.SolveInitialAsync(new SolveRequestDto("same-key", null, null));
        first.Status.Should().Be("Committed");

        await svc.InterruptRoadAsync(new RoadInterruptRequestDto("R2", "flood", "road-evt-1", "road-r2-closed-01"));

        var act = () => svc.SolveInitialAsync(new SolveRequestDto("same-key", null, null));
        var ex = await act.Should().ThrowAsync<SnapshotConflictException>();
        ex.Which.Conflict.FieldDiffs.Should().Contain(f => f.Field == "roadDigest");
    }

    [Fact]
    public async Task SameInputVersion_SamePayloadAndSnapshot_ReplaysSameVersion()
    {
        var svc = _provider.GetRequiredService<IAllocationService>();
        var first = await svc.RearrangeAsync(new SolveRequestDto("replay-key", "reason-x", null, "evt-1"));
        var second = await svc.RearrangeAsync(new SolveRequestDto("replay-key", "reason-x", null, "evt-1"));

        second.Id.Should().Be(first.Id);
        second.RequestPayloadDigest.Should().Be(first.RequestPayloadDigest);
    }

    [Fact]
    public async Task ExpectedDigestMismatch_Returns409WithoutMutating()
    {
        var svc = _provider.GetRequiredService<IAllocationService>();
        var act = () => svc.RearrangeAsync(new SolveRequestDto("mismatch", null, null,
            ExpectedRoadDigest: "deadbeef"));
        var ex = await act.Should().ThrowAsync<SnapshotConflictException>();
        ex.Which.Conflict.FieldDiffs.Should().Contain(f => f.Field == "roadDigest");
    }

    [Fact]
    public async Task RoadEvent_IsRecordedAndReferencedInAudit()
    {
        var svc = _provider.GetRequiredService<IAllocationService>();
        var evt = await svc.InterruptRoadAsync(new RoadInterruptRequestDto(
            "R2", "flooded", "road-v1", "road-r2-closed-01"));
        evt.EventId.Should().Be("road-r2-closed-01");
        evt.RoadSnapshotVersionAfter.Should().BeGreaterThan(evt.RoadSnapshotVersionBefore);

        var v = await svc.RearrangeAsync(new SolveRequestDto("rearrange-v1", "R2 flooded",
            RoadEventId: "road-r2-closed-01"));
        v.TriggeringRoadEventId.Should().Be("road-r2-closed-01");
        v.Audit.Should().Contain(a => a.RoadEventId == "road-r2-closed-01");
    }

    [Fact]
    public async Task EscalateT1ToCritical_ThenRearrange_OnlyT1EligibleForPreemption_T2StaysOnB()
    {
        var svc = _provider.GetRequiredService<IAllocationService>();

        var escalated = await svc.EscalateTaskAsync(new TaskEscalationRequestDto(
            "T1", "LifeSafety", "esc-v1", "Critical flood rescue"));
        escalated.Severity.Should().Be("LifeSafety");
        escalated.SeverityVersion.Should().Be(2);

        var v = await svc.RearrangeAsync(new SolveRequestDto("r2-critical-v1",
            "T1 critical after R2 closure", RoadEventId: null));

        var t1 = v.Assignments.Single(a => a.TaskCode == "T1");
        var t2 = v.Assignments.Single(a => a.TaskCode == "T2");
        t2.TeamCode.Should().Be("B");
        t2.Decision.Should().Be("Kept");
        t2.Preempted.Should().BeFalse();
        v.Audit.Should().Contain(a => a.TaskCode == "T2" && a.Message.Contains("cannot be preempted"));
    }

    [Fact]
    public async Task T2Preempted_WhenRouteBlockedAndFullyCapableReplacementExists()
    {
        var svc = _provider.GetRequiredService<IAllocationService>();
        await svc.InterruptRoadAsync(new RoadInterruptRequestDto(
            "RB", "Landslide blocks access", "rb-block", "road-rb-blocked-01"));
        await svc.EscalateTaskAsync(new TaskEscalationRequestDto(
            "T2", "LifeSafety", "esc-t2", "Slope collapse with trapped person"));

        var v = await svc.RearrangeAsync(new SolveRequestDto("t2-preempt", "RB blocked; T2 critical",
            RoadEventId: "road-rb-blocked-01"));

        var t2 = v.Assignments.Single(a => a.TaskCode == "T2");
        t2.Decision.Should().Be("Reassigned");
        t2.Preempted.Should().BeTrue();
        t2.TeamCode.Should().Be("C");
        t2.PreviousTeamId.Should().Be(TestSeed.TeamBId);
        v.Audit.Should().Contain(a => a.Kind == "task-reassigned" && a.TaskCode == "T2");
    }

    [Fact]
    public async Task ExecutedTask_NotMovedWhenEtaShorterAfterRoadReopens()
    {
        var svc = _provider.GetRequiredService<IAllocationService>();

        await svc.InterruptRoadAsync(new RoadInterruptRequestDto("R2", "flood", "r2-closed", "road-r2-closed-01"));
        var blocked = await svc.RearrangeAsync(new SolveRequestDto("round2", "R2 closed",
            RoadEventId: "road-r2-closed-01"));
        blocked.Status.Should().Be("NoFeasibleSolution");

        await svc.ReopenRoadAsync(new RoadReopenRequestDto("R2", "flood receded", "r2-open", "road-r2-open-02"));

        var started = svc.StartTaskAsync(new TaskStartRequestDto("T1", "start-t1", Guid.NewGuid()));
        await started;

        var reopened = await svc.RearrangeAsync(new SolveRequestDto("r2-reopened-v2", "R2 reopened",
            RoadEventId: "road-r2-open-02"));
        reopened.Status.Should().Be("Committed");

        var t1 = reopened.Assignments.Single(a => a.TaskCode == "T1");
        t1.Decision.Should().Be("Kept");
        t1.Preempted.Should().BeFalse();
    }

    [Fact]
    public async Task CurrentSnapshots_DigestChangesAfterRoadInterrupt()
    {
        var svc = _provider.GetRequiredService<IAllocationService>();
        var before = await svc.GetCurrentDigestsAsync();
        await svc.InterruptRoadAsync(new RoadInterruptRequestDto("R1", "accident", "r-v2", "evt-r1"));
        var after = await svc.GetCurrentDigestsAsync();
        after.Road.Should().NotBe(before.Road);
        after.RoadSnapshotVersion.Should().BeGreaterThan(before.RoadSnapshotVersion);
    }

    [Fact]
    public async Task OldSnapshotMissingRoadEvent_Returns409ViaExpectedDigest()
    {
        var svc = _provider.GetRequiredService<IAllocationService>();
        var before = await svc.GetCurrentDigestsAsync();

        await svc.InterruptRoadAsync(new RoadInterruptRequestDto("R2", "flood", "r2-closed", "road-r2-closed-01"));

        var act = () => svc.RearrangeAsync(new SolveRequestDto("stale-snapshot", "stale",
            ExpectedRoadDigest: before.Road,
            ExpectedRoadSnapshotVersion: before.RoadSnapshotVersion));
        var ex = await act.Should().ThrowAsync<SnapshotConflictException>();
        ex.Which.Conflict.FieldDiffs.Should().Contain(f => f.Field == "roadDigest");
        ex.Which.Conflict.FieldDiffs.Should().Contain(f => f.Field == "roadSnapshotVersion");
    }

    [Fact]
    public async Task DiffVersions_ReturnsTraceableAssignmentAndSnapshotChanges()
    {
        var svc = _provider.GetRequiredService<IAllocationService>();
        var round2 = await svc.RearrangeAsync(new SolveRequestDto("diff-round2", "initial rearrange"));

        await svc.InterruptRoadAsync(new RoadInterruptRequestDto("R2", "flood", "r2-close-diff", "road-r2-closed-diff"));
        await svc.EscalateTaskAsync(new TaskEscalationRequestDto("T1", "LifeSafety", "esc-diff", "critical"));
        var round3 = await svc.RearrangeAsync(new SolveRequestDto("diff-round3", "after event",
            RoadEventId: "road-r2-closed-diff"));

        var diff = await svc.DiffVersionsAsync(round2.Id, round3.Id);
        diff.FromVersionId.Should().Be(round2.Id);
        diff.ToVersionId.Should().Be(round3.Id);
        diff.SnapshotDiffs.Should().Contain(s => s.Field == "roadDigest"
            || s.Field == "taskDigest");
        diff.AssignmentDiffs.Should().NotBeEmpty();
        diff.Summary.Should().Contain("assignment difference");
    }

    public void Dispose()
    {
        _db.Dispose();
        _provider.Dispose();
    }

    private sealed class ThrowingSolver : IAllocationSolver
    {
        public string Name => "throwing";
        public SolverResult Solve(SolverRequest request) =>
            throw new InvalidOperationException("simulated solver failure");
    }
}
