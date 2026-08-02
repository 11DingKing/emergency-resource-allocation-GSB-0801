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
                Id = TestSeed.TaskLifeId, Code = "LIFE-001", Title = "Life",
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
                Id = TestSeed.TaskSlopeId, Code = "SLOPE-009", Title = "Slope",
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
        second.Assignments.Should().Contain(a => a.TaskCode == "LIFE-001" && a.TeamCode == "C");
    }

    [Fact]
    public async Task RoadInterruptThenRearrange_ProducesUnassignedLifeTask_AndAuditConsistent()
    {
        var svc = _provider.GetRequiredService<IAllocationService>();

        await svc.InterruptRoadAsync(new RoadInterruptRequestDto("R2", "Flooded", "road-v2"));

        var after = await svc.RearrangeAsync(new SolveRequestDto("v-after-road", "R2 flooded", null));
        after.Status.Should().Be("NoFeasibleSolution");
        after.RoadSnapshotVersion.Should().Be(2);

        var life = after.Assignments.Single(a => a.TaskCode == "LIFE-001");
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
        result.Audit.Should().Contain(a => a.Kind == "task-assigned" && a.TaskCode == "LIFE-001");
        result.Audit.Should().Contain(a => a.Kind == "candidate-reject" && a.VehicleCode == "VA");

        var lifeAssignment = result.Assignments.Single(a => a.TaskCode == "LIFE-001");
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
        var life = await verify.Tasks.SingleAsync(t => t.Code == "LIFE-001");
        life.AssignedTeamId.Should().BeNull();
        life.Status.Should().Be(TaskStatus.Pending);
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
