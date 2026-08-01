using System.Collections.Concurrent;
using System.IO;
using System.Linq.Expressions;
using EmergencyAllocation.Core;
using EmergencyAllocation.Core.Entities;
using EmergencyAllocation.Core.Solving;
using EmergencyAllocation.Infrastructure.Persistence;
using EmergencyAllocation.Infrastructure.Services.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EmergencyAllocation.Infrastructure.Services;

public sealed class AllocationService : IAllocationService
{
    private const int MaxRetries = 5;
    private readonly IDbContextFactory<AllocationDbContext> _dbContextFactory;
    private readonly ISchedulingSolver _solver;
    private readonly ILogger<AllocationService> _logger;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> VersionLocks = new();

    public AllocationService(
        IDbContextFactory<AllocationDbContext> dbContextFactory,
        ISchedulingSolver solver,
        ILogger<AllocationService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _solver = solver;
        _logger = logger;
    }

    public Task<AllocationResult> SolveInitialAsync(InitialSolveRequest request, CancellationToken cancellationToken = default)
    {
        return SolveWithIdempotencyAsync(
            request.InputVersion,
            previousVersionId: null,
            dangerLevelRaised: false,
            allowPreemption: false,
            cancellationToken);
    }

    public Task<AllocationResult> RearrangeAsync(RearrangeRequest request, CancellationToken cancellationToken = default)
    {
        return SolveWithIdempotencyAsync(
            request.InputVersion,
            previousVersionId: request.PreviousVersionId,
            dangerLevelRaised: request.DangerLevelRaised,
            allowPreemption: true,
            cancellationToken);
    }

    public async Task<AllocationResult?> GetVersionAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var version = await LoadVersionAsync(context, v => v.Id == versionId, cancellationToken);
        return version is null ? null : Map(version);
    }

    public async Task<AllocationResult?> GetByInputVersionAsync(string inputVersion, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var version = await LoadVersionAsync(context, v => v.InputVersion == inputVersion, cancellationToken);
        return version is null ? null : Map(version);
    }

    public async Task<AllocationResult?> GetLatestCommittedAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var version = await context.AllocationVersions
            .AsNoTracking()
            .Include(v => v.Assignments)
            .Include(v => v.Explanations)
            .Where(v => v.Status == AllocationStatus.Committed)
            .OrderByDescending(v => v.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return version is null ? null : Map(version);
    }

    private async Task<AllocationResult> SolveWithIdempotencyAsync(
        string inputVersion,
        Guid? previousVersionId,
        bool dangerLevelRaised,
        bool allowPreemption,
        CancellationToken cancellationToken)
    {
        var gate = VersionLocks.GetOrAdd(inputVersion, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await GetByInputVersionAsync(inputVersion, cancellationToken);
            if (existing is not null)
            {
                _logger.LogInformation("Input version {InputVersion} already committed as {VersionId}; returning idempotent result.", inputVersion, existing.VersionId);
                return existing;
            }

            for (var attempt = 0; attempt < MaxRetries; attempt++)
            {
                try
                {
                    var (teams, tasks, roads) = await LoadSnapshotAsync(cancellationToken);
                    var options = new SolverOptions(dangerLevelRaised, allowPreemption, _solver.Version);
                    var problem = SnapshotBuilder.Build(teams, tasks, roads, options, previousVersionId, inputVersion);
                    var snapshotHash = SnapshotBuilder.ComputeHash(problem);

                    _logger.LogInformation("Solver attempt {Attempt} for input version {InputVersion} with snapshot {Hash}.", attempt, inputVersion, snapshotHash);

                    var solverResult = await _solver.SolveAsync(problem, cancellationToken);

                    var version = BuildVersionEntity(problem, solverResult, snapshotHash);
                    await PersistVersionAsync(version, cancellationToken);
                    return Map(version);
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex))
                {
                    _logger.LogWarning(ex, "Unique violation for input version {InputVersion}; reading winner.", inputVersion);
                    var winner = await GetByInputVersionAsync(inputVersion, cancellationToken);
                    if (winner is not null)
                    {
                        return winner;
                    }
                }
                catch (Exception ex) when (IsTransient(ex) && attempt < MaxRetries - 1)
                {
                    var delay = TimeSpan.FromMilliseconds(50 * Math.Pow(2, attempt));
                    _logger.LogWarning(ex, "Transient failure on attempt {Attempt} for {InputVersion}; retrying in {Delay}ms.", attempt, inputVersion, delay.TotalMilliseconds);
                    await Task.Delay(delay, cancellationToken);
                }
            }

            throw new InvalidOperationException($"Failed to commit allocation for input version '{inputVersion}' after {MaxRetries} attempts.");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(IReadOnlyList<Team> Teams, IReadOnlyList<EmergencyTask> Tasks, IReadOnlyList<RoadSegment> Roads)> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var teams = await context.Teams
            .AsNoTracking()
            .Include(t => t.Vehicle)
            .ToListAsync(cancellationToken);
        var tasks = await context.Tasks
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var roads = await context.RoadSegments
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return (teams, tasks, roads);
    }

    private async Task PersistVersionAsync(AllocationVersion version, CancellationToken cancellationToken)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        try
        {
            var duplicate = await context.AllocationVersions
                .AsNoTracking()
                .AnyAsync(v => v.InputVersion == version.InputVersion, cancellationToken);
            if (duplicate)
            {
                throw new InvalidOperationException($"Input version '{version.InputVersion}' already exists.");
            }

            context.AllocationVersions.Add(version);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static AllocationVersion BuildVersionEntity(
        SchedulingProblem problem,
        SolverResult solverResult,
        string snapshotHash)
    {
        var version = new AllocationVersion
        {
            Id = Guid.NewGuid(),
            InputVersion = problem.InputVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = AllocationStatus.Committed,
            DangerLevelRaised = problem.Options.DangerLevelRaised,
            SnapshotTakenAt = problem.SnapshotTakenAt,
            SnapshotHash = snapshotHash,
            SolverVersion = solverResult.SolverVersion,
            TotalCost = solverResult.TotalCost,
            IsFeasible = solverResult.IsFeasible,
            NoFeasibleReason = solverResult.NoFeasibleReason,
            PreviousVersionId = problem.PreviousVersionId
        };

        version.Assignments = solverResult.Assignments
            .Select((a, i) => new TaskAssignment
            {
                Id = Guid.NewGuid(),
                AllocationVersionId = version.Id,
                TaskId = a.TaskId,
                TeamId = a.TeamId ?? string.Empty,
                VehicleId = a.VehicleId ?? string.Empty,
                RouteNodes = a.RouteNodes.ToList(),
                EstimatedArrivalMinutes = a.EstimatedArrivalMinutes,
                EstimatedCompletionMinutes = a.EstimatedCompletionMinutes,
                Kind = a.Kind,
                PreemptionReason = a.PreemptionReason,
                OrderIndex = i
            })
            .ToList();

        version.Explanations = solverResult.Explanations
            .Select(e => new AuditExplanation
            {
                Id = Guid.NewGuid(),
                AllocationVersionId = version.Id,
                Kind = e.Kind,
                RuleCode = e.RuleCode,
                Message = e.Message,
                RelatedTaskId = e.RelatedTaskId,
                RelatedTeamId = e.RelatedTeamId
            })
            .ToList();

        return version;
    }

    private static async Task<AllocationVersion?> LoadVersionAsync(
        AllocationDbContext context,
        Expression<Func<AllocationVersion, bool>> predicate,
        CancellationToken cancellationToken)
    {
        return await context.AllocationVersions
            .AsNoTracking()
            .Include(v => v.Assignments)
            .Include(v => v.Explanations)
            .Where(predicate)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static AllocationResult Map(AllocationVersion version)
    {
        return new AllocationResult(
            version.Id,
            version.InputVersion,
            version.IsFeasible,
            version.NoFeasibleReason,
            version.TotalCost,
            version.SolverVersion,
            version.CreatedAt,
            version.SnapshotHash,
            version.Assignments
                .OrderBy(a => a.OrderIndex)
                .Select(a => new AssignmentDto(
                    a.TaskId,
                    string.IsNullOrEmpty(a.TeamId) ? null : a.TeamId,
                    string.IsNullOrEmpty(a.VehicleId) ? null : a.VehicleId,
                    a.RouteNodes,
                    a.EstimatedArrivalMinutes,
                    a.EstimatedCompletionMinutes,
                    a.Kind.ToString(),
                    a.PreemptionReason,
                    a.OrderIndex))
                .ToList(),
            version.Explanations
                .Select(e => new ExplanationDto(
                    e.Kind.ToString(),
                    e.RuleCode,
                    e.Message,
                    e.RelatedTaskId,
                    e.RelatedTeamId))
                .ToList());
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation;
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is DbUpdateException { InnerException: PostgresException pg })
        {
            return pg.SqlState is PostgresErrorCodes.SerializationFailure
                or PostgresErrorCodes.DeadlockDetected
                or "08006" // connection failure
                or "08001"; // SQL client unable to establish connection
        }

        if (ex is TimeoutException or IOException)
        {
            return true;
        }

        return false;
    }
}
