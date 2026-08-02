using EmergencyAllocation.Domain;
using EmergencyAllocation.Domain.Dtos;
using EmergencyAllocation.Domain.Services;
using EmergencyAllocation.Domain.Solver;
using EmergencyAllocation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TaskStatus = EmergencyAllocation.Domain.TaskStatus;

using System.Collections.Concurrent;

namespace EmergencyAllocation.Infrastructure.Services;

public sealed class AllocationService : IAllocationService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> KeyedLocks = new();

    private readonly IDbContextFactory<AllocationDbContext> _contextFactory;
    private readonly IAllocationSolver _solver;
    private readonly ILogger<AllocationService> _logger;

    public AllocationService(
        IDbContextFactory<AllocationDbContext> contextFactory,
        IAllocationSolver solver,
        ILogger<AllocationService> logger)
    {
        _contextFactory = contextFactory;
        _solver = solver;
        _logger = logger;
    }

    public Task<AllocationVersionDto> SolveInitialAsync(SolveRequestDto request, CancellationToken ct = default)
        => SolveCoreAsync("initial", request, allowReassign: false, ct);

    public Task<AllocationVersionDto> RearrangeAsync(SolveRequestDto request, CancellationToken ct = default)
        => SolveCoreAsync("rearrange", request, allowReassign: true, ct);

    private async Task<AllocationVersionDto> SolveCoreAsync(
        string operation,
        SolveRequestDto request,
        bool allowReassign,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.InputVersion))
            throw new ArgumentException("InputVersion is required for idempotency.", nameof(request));

        var idempotencyKey = BuildIdempotencyKey(operation, request.InputVersion);
        var keyLock = KeyedLocks.GetOrAdd(idempotencyKey, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(ct);
        try
        {
            return await SolveCoreTransactionalAsync(operation, request, allowReassign, idempotencyKey, ct);
        }
        finally
        {
            keyLock.Release();
        }
    }

    private async Task<AllocationVersionDto> SolveCoreTransactionalAsync(
        string operation,
        SolveRequestDto request,
        bool allowReassign,
        string idempotencyKey,
        CancellationToken ct)
    {

        for (int attempt = 0; attempt < 3; attempt++)
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
            await using var tx = await ctx.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, ct);

            try
            {
                var existing = await ctx.AllocationVersions
                    .Include(v => v.Assignments)
                    .Include(v => v.AuditEntries)
                    .Where(v => v.Operation == operation && v.IdempotencyKey == idempotencyKey)
                    .FirstOrDefaultAsync(ct);

                if (existing is not null && IsTerminal(existing.Status))
                {
                    await tx.RollbackAsync(ct);
                    _logger.LogInformation(
                        "Idempotent hit on {Operation} key={Key} version={VersionId} status={Status}",
                        operation, idempotencyKey, existing.Id, existing.Status);
                    return await MapVersionAsync(existing.Id, ct);
                }

                if (existing is not null && existing.Status == AllocationVersionStatus.Pending)
                {
                    ctx.AllocationVersions.Remove(existing);
                    await ctx.SaveChangesAsync(ct);
                }

                var roadSnapshotVersion = await ctx.RoadSegments
                    .MaxAsync(r => (long?)r.RoadSnapshotVersion, ct) ?? 0L;

                var snapshot = await BuildSnapshotAsync(ctx, roadSnapshotVersion, ct);
                var solverRequest = new SolverRequest(
                    snapshot.Teams,
                    snapshot.Tasks,
                    snapshot.Roads,
                    roadSnapshotVersion,
                    operation,
                    allowReassign,
                    request.Reason);

                SolverResult result;
                try
                {
                    result = _solver.Solve(solverRequest);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Solver failed for operation {Operation}", operation);
                    var failed = new AllocationVersion
                    {
                        Id = Guid.NewGuid(),
                        Operation = operation,
                        InputVersion = request.InputVersion,
                        IdempotencyKey = idempotencyKey,
                        Status = AllocationVersionStatus.Failed,
                        RoadSnapshotVersion = roadSnapshotVersion,
                        CreatedAt = DateTimeOffset.UtcNow,
                        FailureReason = Truncate(ex.Message, 1024)
                    };
                    ctx.AllocationVersions.Add(failed);
                    await ctx.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    return await MapVersionAsync(failed.Id, ct);
                }

                var version = new AllocationVersion
                {
                    Id = Guid.NewGuid(),
                    Operation = operation,
                    InputVersion = request.InputVersion,
                    IdempotencyKey = idempotencyKey,
                    Status = result.Feasible
                        ? AllocationVersionStatus.Committed
                        : AllocationVersionStatus.NoFeasibleSolution,
                    RoadSnapshotVersion = roadSnapshotVersion,
                    CreatedAt = DateTimeOffset.UtcNow,
                    CommittedAt = result.Feasible ? DateTimeOffset.UtcNow : null,
                    TotalCostMinutes = result.TotalCostMinutes,
                    AssignedCount = result.Assignments.Count(a =>
                        a.Decision is AllocationDecision.Assigned
                            or AllocationDecision.Kept
                            or AllocationDecision.Reassigned),
                    UnassignedCount = result.Assignments.Count(a => a.Decision == AllocationDecision.Unassigned)
                };

                foreach (var a in result.Assignments)
                {
                    version.Assignments.Add(new Assignment
                    {
                        Id = Guid.NewGuid(),
                        TaskId = a.TaskId,
                        TeamId = a.TeamId,
                        VehicleId = a.VehicleId,
                        Decision = a.Decision,
                        EstimatedTravelMinutes = a.EstimatedTravelMinutes,
                        EstimatedTotalMinutes = a.EstimatedTotalMinutes,
                        MeetsDeadline = a.MeetsDeadline,
                        RouteNodeIds = a.Route is null ? null : string.Join(">", a.Route.NodeIds),
                        Reason = a.Reason,
                        Preempted = a.Preempted,
                        PreviousTeamId = a.PreviousTeamId,
                        PreviousVehicleId = a.PreviousVehicleId
                    });
                }

                foreach (var au in result.Audit)
                {
                    version.AuditEntries.Add(new AllocationAuditEntry
                    {
                        Id = Guid.NewGuid(),
                        Order = au.Order,
                        Kind = au.Kind,
                        TaskCode = au.TaskCode,
                        TeamCode = au.TeamCode,
                        VehicleCode = au.VehicleCode,
                        RoadCode = au.RoadCode,
                        Message = au.Message
                    });
                }

                ctx.AllocationVersions.Add(version);

                if (result.Feasible)
                {
                    var taskDict = snapshot.Tasks.ToDictionary(t => t.Id);
                    foreach (var a in result.Assignments
                                 .Where(x => x.Decision is AllocationDecision.Assigned
                                     or AllocationDecision.Reassigned))
                    {
                        var dbTask = await ctx.Tasks.FindAsync(new object?[] { a.TaskId }, ct);
                        if (dbTask is null) continue;
                        dbTask.AssignedTeamId = a.TeamId;
                        dbTask.AssignedVehicleId = a.VehicleId;
                        if (a.Decision == AllocationDecision.Reassigned)
                        {
                            dbTask.Status = TaskStatus.Pending;
                            dbTask.StartedAt = null;
                        }
                        else if (dbTask.Status == TaskStatus.Pending)
                        {
                            dbTask.Status = TaskStatus.InProgress;
                            dbTask.StartedAt ??= DateTimeOffset.UtcNow;
                        }
                    }

                    var latestCommitted = await ctx.AllocationVersions
                        .Where(v => v.Status == AllocationVersionStatus.Committed && v.Id != version.Id)
                        .OrderByDescending(v => v.Sequence)
                        .FirstOrDefaultAsync(ct);
                    if (latestCommitted is not null)
                        latestCommitted.Status = AllocationVersionStatus.Superseded;
                }

                await ctx.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                _logger.LogInformation(
                    "Committed allocation version {VersionId} op={Op} key={Key} feasible={Feas}",
                    version.Id, operation, idempotencyKey, result.Feasible);

                return await MapVersionAsync(version.Id, ct);
            }
            catch (DbUpdateException ex) when (IsTransient(ex) && attempt < 2)
            {
                _logger.LogWarning(ex,
                    "Transient update conflict on {Operation} attempt {Attempt}; retrying.", operation, attempt);
                await tx.RollbackAsync(ct);
                continue;
            }
            catch (NpgsqlException ex) when (ex.IsTransient && attempt < 2)
            {
                _logger.LogWarning(ex,
                    "Transient Npgsql failure on {Operation} attempt {Attempt}; retrying.", operation, attempt);
                await tx.RollbackAsync(ct);
                continue;
            }
        }

        throw new InvalidOperationException(
            $"Failed to commit allocation for {operation} after retries (idempotency key={idempotencyKey}).");
    }

    public async Task<AllocationVersionDto?> GetVersionAsync(Guid versionId, CancellationToken ct = default)
        => await MapVersionAsync(versionId, ct);

    public async Task<AllocationVersionDto?> GetLatestCommittedAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var v = await ctx.AllocationVersions
            .Where(x => x.Status == AllocationVersionStatus.Committed)
            .OrderByDescending(x => x.Sequence)
            .FirstOrDefaultAsync(ct);
        return v is null ? null : await MapVersionAsync(v.Id, ct);
    }

    public async Task InterruptRoadAsync(RoadInterruptRequestDto request, CancellationToken ct = default)
        => await ChangeRoadAsync(request.RoadCode, false, request.Reason ?? "Road interrupted", request.InputVersion, ct);

    public async Task ReopenRoadAsync(RoadReopenRequestDto request, CancellationToken ct = default)
        => await ChangeRoadAsync(request.RoadCode, true, request.Reason ?? "Road reopened", request.InputVersion, ct);

    private async Task ChangeRoadAsync(
        string roadCode, bool isOpen, string reason, string inputVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(inputVersion))
            throw new ArgumentException("InputVersion is required.", nameof(inputVersion));

        for (int attempt = 0; attempt < 3; attempt++)
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
            await using var tx = await ctx.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, ct);

            try
            {
                var road = await ctx.RoadSegments.FirstOrDefaultAsync(r => r.Code == roadCode, ct);
                if (road is null)
                    throw new KeyNotFoundException($"Road {roadCode} not found.");

                road.IsOpen = isOpen;
                road.InterruptionReason = isOpen ? null : reason;
                road.UpdatedAt = DateTimeOffset.UtcNow;

                var nextVersion = (await ctx.RoadSegments.MaxAsync(r => (long?)r.RoadSnapshotVersion, ct) ?? 0L) + 1;
                road.RoadSnapshotVersion = nextVersion;

                await ctx.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (IsTransient(ex) && attempt < 2)
            {
                await tx.RollbackAsync(ct);
                continue;
            }
        }
        throw new InvalidOperationException("Failed to update road after retries.");
    }

    private static bool IsTerminal(AllocationVersionStatus s) =>
        s is AllocationVersionStatus.Committed
            or AllocationVersionStatus.NoFeasibleSolution
            or AllocationVersionStatus.Failed;

    private static bool IsTransient(DbUpdateException ex)
    {
        if (ex.InnerException is NpgsqlException npgsql)
            return npgsql.IsTransient
                || npgsql.SqlState == PostgresErrorCodes.SerializationFailure
                || npgsql.SqlState == PostgresErrorCodes.UniqueViolation;
        return false;
    }

    private static string BuildIdempotencyKey(string operation, string inputVersion)
        => $"{operation}:{inputVersion}";

    private static string? Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? s : s.Length <= max ? s : s[..max];

    private async Task<AllocationVersionDto> MapVersionAsync(Guid versionId, CancellationToken ct)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var v = await ctx.AllocationVersions
            .AsNoTracking()
            .Include(x => x.Assignments).ThenInclude(a => a.Task)
            .Include(x => x.Assignments).ThenInclude(a => a.Team)
            .Include(x => x.Assignments).ThenInclude(a => a.Vehicle)
            .Include(x => x.AuditEntries)
            .FirstOrDefaultAsync(x => x.Id == versionId, ct);

        if (v is null) throw new KeyNotFoundException($"Allocation version {versionId} not found.");
        return ToDto(v);
    }

    private async Task<AllocationVersionDto?> MapLatestAsync(CancellationToken ct)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var v = await ctx.AllocationVersions
            .AsNoTracking()
            .Include(x => x.Assignments).ThenInclude(a => a.Task)
            .Include(x => x.Assignments).ThenInclude(a => a.Team)
            .Include(x => x.Assignments).ThenInclude(a => a.Vehicle)
            .Include(x => x.AuditEntries)
            .OrderByDescending(x => x.Sequence)
            .FirstOrDefaultAsync(ct);
        return v is null ? null : ToDto(v);
    }

    private static AllocationVersionDto ToDto(AllocationVersion v) => new(
        v.Id,
        v.Operation,
        v.InputVersion,
        v.IdempotencyKey,
        v.Status.ToString(),
        v.Sequence,
        v.CreatedAt,
        v.CommittedAt,
        v.RoadSnapshotVersion,
        v.TotalCostMinutes,
        v.AssignedCount,
        v.UnassignedCount,
        v.FailureReason,
        v.Assignments
            .OrderBy(a => a.Task?.Code ?? string.Empty, StringComparer.Ordinal)
            .Select(a => new AssignmentDto(
                a.TaskId,
                a.Task?.Code ?? string.Empty,
                a.Task?.Title ?? string.Empty,
                a.TeamId,
                a.Team?.Code,
                a.VehicleId,
                a.Vehicle?.Code,
                a.Decision.ToString(),
                a.EstimatedTravelMinutes,
                a.EstimatedTotalMinutes,
                a.MeetsDeadline,
                a.RouteNodeIds?.Split('>', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>(),
                a.Reason,
                a.Preempted,
                a.PreviousTeamId,
                a.PreviousVehicleId))
            .ToList(),
        v.AuditEntries.OrderBy(a => a.Order).Select(a => new AuditEntryDto(
            a.Order, a.Kind, a.TaskCode, a.TeamCode, a.VehicleCode, a.RoadCode, a.Message)).ToList());

    private static async Task<SolverSnapshot> BuildSnapshotAsync(
        AllocationDbContext ctx, long snapshotVersion, CancellationToken ct)
    {
        var teams = await ctx.Teams
            .AsNoTracking()
            .Include(t => t.Capabilities)
            .Include(t => t.Vehicles)
            .ToListAsync(ct);

        var tasks = await ctx.Tasks
            .AsNoTracking()
            .Include(t => t.RequiredCapabilities)
            .ToListAsync(ct);

        var roads = await ctx.RoadSegments
            .AsNoTracking()
            .Where(r => r.RoadSnapshotVersion <= snapshotVersion || r.RoadSnapshotVersion == 0)
            .ToListAsync(ct);

        var latestByCode = roads
            .GroupBy(r => r.Code)
            .Select(g => g.OrderByDescending(r => r.RoadSnapshotVersion).First())
            .ToList();

        var solverTeams = teams.Select(t => new SolverTeam(
            t.Id, t.Code, t.BaseNodeId, t.IsAvailable,
            t.Capabilities.Select(c => c.Capability).ToHashSet(StringComparer.Ordinal),
            t.Vehicles.Select(v => new SolverVehicle(
                v.Id, v.Code, v.TeamId, v.HeightMeters, v.AverageSpeedMetersPerMinute, v.IsAvailable)).ToList())).ToList();

        var solverTasks = tasks.Select(t => new SolverTask(
            t.Id, t.Code, t.Title, t.LocationNodeId, t.Severity, t.SeverityVersion, t.Status,
            t.DurationMinutes, t.DeadlineMinutes,
            t.RequiredCapabilities.Select(r => r.Capability).ToHashSet(StringComparer.Ordinal),
            t.AssignedTeamId, t.AssignedVehicleId,
            t.Status == TaskStatus.InProgress && t.StartedAt.HasValue)).ToList();

        var solverRoads = latestByCode.Select(r => new SolverRoad(
            r.Code, r.FromNodeId, r.ToNodeId, r.TravelTimeMinutes,
            r.HeightLimitMeters, r.IsOpen, r.RoadSnapshotVersion)).ToList();

        return new SolverSnapshot(solverTeams, solverTasks, solverRoads);
    }

    private sealed record SolverSnapshot(
        IReadOnlyList<SolverTeam> Teams,
        IReadOnlyList<SolverTask> Tasks,
        IReadOnlyList<SolverRoad> Roads);
}
