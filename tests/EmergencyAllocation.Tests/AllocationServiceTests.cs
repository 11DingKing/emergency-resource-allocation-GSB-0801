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

public class AllocationServiceTests
{
    private static readonly string DbName = Guid.NewGuid().ToString();

    private static async Task<InMemoryAllocationDbContextFactory> CreateSeededFactoryAsync(string? name = null)
    {
        var factory = new InMemoryAllocationDbContextFactory(name ?? DbName);
        await using var context = factory.CreateDbContext();
        await context.Database.EnsureDeletedAsync();
        await TestSeed.SeedAsync(context);
        return factory;
    }

    [Fact]
    public async Task Idempotency_SameInputVersionReturnsSameVersion()
    {
        var factory = await CreateSeededFactoryAsync(Guid.NewGuid().ToString());
        var service = new AllocationService(factory, new DeterministicSchedulingSolver(), NullLogger<AllocationService>.Instance);

        var first = await service.SolveInitialAsync(new InitialSolveRequest("v-1"));
        var second = await service.SolveInitialAsync(new InitialSolveRequest("v-1"));

        Assert.Equal(first.VersionId, second.VersionId);
        await using var context = factory.CreateDbContext();
        var count = await context.AllocationVersions.CountAsync(v => v.InputVersion == "v-1");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ConcurrentSameInputVersion_ProducesSingleCommittedVersion()
    {
        var factory = await CreateSeededFactoryAsync(Guid.NewGuid().ToString());
        var service = new AllocationService(factory, new DeterministicSchedulingSolver(), NullLogger<AllocationService>.Instance);

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => service.SolveInitialAsync(new InitialSolveRequest("concurrent-v"))))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        var uniqueIds = results.Select(r => r.VersionId).Distinct().Count();
        Assert.Equal(1, uniqueIds);

        await using var context = factory.CreateDbContext();
        var count = await context.AllocationVersions.CountAsync(v => v.InputVersion == "concurrent-v");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task TransientCommitFailure_RetriesAndCommitsExactlyOnce()
    {
        var factory = new InMemoryAllocationDbContextFactory(Guid.NewGuid().ToString());
        await using (var seedContext = factory.CreateDbContext())
        {
            await TestSeed.SeedAsync(seedContext);
        }

        factory.ArmTransientFailures(1);
        var service = new AllocationService(factory, new DeterministicSchedulingSolver(), NullLogger<AllocationService>.Instance);

        var result = await service.SolveInitialAsync(new InitialSolveRequest("retry-v"));

        Assert.NotNull(result);
        await using var context = factory.CreateDbContext();
        var count = await context.AllocationVersions.CountAsync(v => v.InputVersion == "retry-v");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task NonTransientFailure_NoPartialAllocationVisible()
    {
        var factory = new InMemoryAllocationDbContextFactory(Guid.NewGuid().ToString());
        await using (var seedContext = factory.CreateDbContext())
        {
            await TestSeed.SeedAsync(seedContext);
        }

        factory.ArmPermanentFailure();
        var service = new AllocationService(factory, new DeterministicSchedulingSolver(), NullLogger<AllocationService>.Instance);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            service.SolveInitialAsync(new InitialSolveRequest("atomic-v")));

        await using var context = factory.CreateDbContext();
        Assert.Equal(0, await context.AllocationVersions.CountAsync());
        Assert.Equal(0, await context.TaskAssignments.CountAsync());
        Assert.Equal(0, await context.AuditExplanations.CountAsync());
    }

    [Fact]
    public async Task RoadUpdatedMidSolve_CommitsVersionBasedOnCapturedSnapshot()
    {
        var factory = await CreateSeededFactoryAsync(Guid.NewGuid().ToString());
        var signaling = new SignalingSolver(new DeterministicSchedulingSolver());
        var service = new AllocationService(factory, signaling, NullLogger<AllocationService>.Instance);

        var solveTask = Task.Run(() => service.SolveInitialAsync(new InitialSolveRequest("mid-v")));

        await signaling.Captured;
        Assert.True(signaling.CapturedProblem!.Roads.Single(r => r.Id == "R1").IsOpen);

        await using (var updateContext = factory.CreateDbContext())
        {
            var r1 = await updateContext.RoadSegments.SingleAsync(r => r.Id == "R1");
            r1.IsOpen = false;
            await updateContext.SaveChangesAsync();
        }

        signaling.Release();
        var result = await solveTask;

        Assert.Equal(SnapshotBuilder.ComputeHash(signaling.CapturedProblem!), result.SnapshotHash);
        var t1 = Assert.Single(result.Assignments, a => a.TaskId == "T1");
        Assert.Equal("C", t1.TeamId);
        Assert.Equal(20, t1.EstimatedArrivalMinutes);

        var freshService = new AllocationService(factory, new DeterministicSchedulingSolver(), NullLogger<AllocationService>.Instance);
        var after = await freshService.SolveInitialAsync(new InitialSolveRequest("mid-v-after"));
        var t1After = Assert.Single(after.Assignments, a => a.TaskId == "T1");
        Assert.Equal(35, t1After.EstimatedArrivalMinutes);
        Assert.Contains("SLOPE", (IEnumerable<string>)t1After.RouteNodes);
    }

    [Fact]
    public async Task Rearrange_AfterRoadClosureAndDangerRaised_PreemptsCToA()
    {
        var factory = await CreateSeededFactoryAsync(Guid.NewGuid().ToString());
        var service = new AllocationService(factory, new DeterministicSchedulingSolver(), NullLogger<AllocationService>.Instance);

        var initial = await service.SolveInitialAsync(new InitialSolveRequest("scenario-v1"));
        var t1Initial = Assert.Single(initial.Assignments, a => a.TaskId == "T1");
        Assert.Equal("C", t1Initial.TeamId);
        Assert.Equal(20, t1Initial.EstimatedArrivalMinutes);

        await using (var ctx = factory.CreateDbContext())
        {
            var t1 = await ctx.Tasks.SingleAsync(t => t.Id == "T1");
            t1.Status = TaskStatus.InProgress;
            t1.AssignedTeamId = "C";
            var r1 = await ctx.RoadSegments.SingleAsync(r => r.Id == "R1");
            r1.IsOpen = false;
            await ctx.SaveChangesAsync();
        }

        var rearranged = await service.RearrangeAsync(
            new RearrangeRequest("scenario-v2", initial.VersionId, true));

        var t1After = Assert.Single(rearranged.Assignments, a => a.TaskId == "T1");
        Assert.Equal("A", t1After.TeamId);
        Assert.Equal(AssignmentKind.ReassignedTo.ToString(), t1After.Kind);
        Assert.Equal(35, t1After.EstimatedArrivalMinutes);
        Assert.Equal(new[] { "DEPOT", "SLOPE", "WATER" }, t1After.RouteNodes);
        Assert.NotNull(t1After.PreemptionReason);

        var t2After = Assert.Single(rearranged.Assignments, a => a.TaskId == "T2");
        Assert.Equal("B", t2After.TeamId);
        Assert.Equal(AssignmentKind.Kept.ToString(), t2After.Kind);

        Assert.Contains(rearranged.Explanations, e => e.RuleCode == RuleCodes.PreemptionAllowed);
    }

    private sealed class SignalingSolver : ISchedulingSolver
    {
        private readonly ISchedulingSolver _inner;
        private readonly TaskCompletionSource _captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _proceed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public SignalingSolver(ISchedulingSolver inner) => _inner = inner;
        public Task Captured => _captured.Task;
        public SchedulingProblem? CapturedProblem { get; private set; }
        public void Release() => _proceed.TrySetResult();
        public string Version => _inner.Version;

        public async Task<SolverResult> SolveAsync(SchedulingProblem problem, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                CapturedProblem = problem;
                _captured.TrySetResult();
                await _proceed.Task;
            }

            return await _inner.SolveAsync(problem, cancellationToken);
        }
    }
}
