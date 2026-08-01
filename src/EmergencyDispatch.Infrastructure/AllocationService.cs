namespace EmergencyDispatch.Infrastructure;

using System.Data;
using EmergencyDispatch.Domain;
using EmergencyDispatch.Solver;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IAllocationService"/>. Orchestrates read → solve → persist while
/// enforcing three guarantees:
///
/// * <b>Idempotency</b> — keyed on <see cref="SolveRequest.InputVersion"/>. A duplicate
///   input version returns the previously stored version and never re-solves.
/// * <b>Atomicity</b> — the whole allocation version (assignments + unassigned reasons +
///   audit) is written inside one serializable transaction. A commit failure rolls the
///   entire version back, so external readers only ever see complete plans.
/// * <b>Bounded retry</b> — serialization failures and the unique-index race on
///   <c>InputVersion</c> are retried; if a competing writer won the race, the now-committed
///   version is returned rather than duplicated.
///
/// The algorithm itself is injected as <see cref="IAllocationSolver"/>; this class contains
/// no allocation logic.
/// </summary>
public sealed class AllocationService : IAllocationService
{
    private const int MaxAttempts = 5;

    private readonly DispatchDbContext _db;
    private readonly IAllocationSolver _solver;
    private readonly ILogger<AllocationService> _logger;

    public AllocationService(
        DispatchDbContext db,
        IAllocationSolver solver,
        ILogger<AllocationService> logger)
    {
        _db = db;
        _solver = solver;
        _logger = logger;
    }

    public async Task<AllocationResult> SolveAsync(SolveRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.InputVersion))
        {
            throw new ArgumentException("InputVersion is required.", nameof(request));
        }

        // Fast path: already solved for this input version.
        var existing = await GetByInputVersionAsync(request.InputVersion, ct);
        if (existing is not null)
        {
            return new AllocationResult { Version = existing, WasExisting = true };
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return await SolveAndPersistOnceAsync(request, ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // A concurrent writer committed the same input version first. Idempotency
                // wins: return their committed version. Nothing of ours was persisted.
                _logger.LogInformation(
                    "Concurrent submit for input version {InputVersion} lost the race; returning existing version.",
                    request.InputVersion);
                var winner = await GetByInputVersionAsync(request.InputVersion, ct);
                if (winner is not null)
                {
                    return new AllocationResult { Version = winner, WasExisting = true };
                }
                // Extremely unlikely: violation without a readable row. Retry.
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < MaxAttempts)
            {
                _logger.LogWarning(ex,
                    "Transient failure solving input version {InputVersion} (attempt {Attempt}); retrying.",
                    request.InputVersion, attempt);
            }

            // Clear tracked entities before the next attempt so retries start clean.
            _db.ChangeTracker.Clear();
        }

        throw new InvalidOperationException(
            $"Failed to persist allocation for input version '{request.InputVersion}' after {MaxAttempts} attempts.");
    }

    private async Task<AllocationResult> SolveAndPersistOnceAsync(SolveRequest request, CancellationToken ct)
    {
        await using IDbContextTransaction tx =
            await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        // Re-check inside the transaction to close the read-then-write gap.
        var existing = await _db.AllocationVersions
            .AsNoTracking()
            .Include(v => v.Assignments)
            .Include(v => v.UnassignedReasons)
            .Include(v => v.AuditEntries)
            .FirstOrDefaultAsync(v => v.InputVersion == request.InputVersion, ct);
        if (existing is not null)
        {
            await tx.RollbackAsync(ct);
            return new AllocationResult { Version = existing, WasExisting = true };
        }

        // Build the pure snapshot from a consistent read, then solve outside the DB.
        var input = await BuildSnapshotAsync(request, ct);
        SolveResult result = _solver.Solve(input);

        var nextNumber = await NextVersionNumberAsync(ct);
        var version = MapToVersion(request, result, nextNumber, input);

        _db.AllocationVersions.Add(version);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Persisted allocation version {VersionNumber} for input version {InputVersion} ({AssignCount} assignments, {UnassignedCount} unassigned).",
            version.VersionNumber, version.InputVersion,
            version.Assignments.Count, version.UnassignedReasons.Count);

        return new AllocationResult { Version = version, WasExisting = false };
    }

    private async Task<SolveInput> BuildSnapshotAsync(SolveRequest request, CancellationToken ct)
    {
        var teams = await _db.Teams.AsNoTracking().ToListAsync(ct);
        var vehicles = await _db.Vehicles.AsNoTracking().ToListAsync(ct);
        var tasks = await _db.Tasks.AsNoTracking().ToListAsync(ct);
        var roads = await _db.RoadSegments.AsNoTracking().ToListAsync(ct);
        var routes = await _db.Routes.AsNoTracking().ToListAsync(ct);

        // Previous danger level per task from the latest version's audit, to detect escalation.
        var previousDanger = await BuildPreviousDangerMapAsync(ct);

        return new SolveInput
        {
            InputVersion = request.InputVersion,
            IsReplan = request.IsReplan,
            Teams = teams.Select(t => new TeamSnapshot
            {
                Id = t.Id,
                Code = t.Code,
                Capabilities = t.Capabilities,
            }).ToList(),
            Vehicles = vehicles.Select(v => new VehicleSnapshot
            {
                Id = v.Id,
                Code = v.Code,
                HeightMeters = v.HeightMeters,
            }).ToList(),
            Tasks = tasks
                .Where(t => t.Status != TaskStatus.Completed)
                .Select(t => new TaskSnapshot
                {
                    Id = t.Id,
                    Code = t.Code,
                    RequiredCapabilities = t.RequiredCapabilities,
                    DeadlineMinutes = t.DeadlineMinutes,
                    ServiceMinutes = t.ServiceMinutes,
                    DangerLevel = t.DangerLevel,
                    IsInProgress = t.Status == TaskStatus.InProgress,
                    PreviousDangerLevel = previousDanger.TryGetValue(t.Id, out var prev) ? prev : null,
                    ExecutingTeamId = t.ExecutingTeamId,
                    ExecutingVehicleId = t.ExecutingVehicleId,
                }).ToList(),
            Roads = roads.Select(r => new RoadSnapshot
            {
                Id = r.Id,
                Code = r.Code,
                HeightLimitMeters = r.HeightLimitMeters,
                IsOpen = r.IsOpen,
            }).ToList(),
            Routes = routes.Select(r => new RouteSnapshot
            {
                Id = r.Id,
                TaskId = r.TaskId,
                RoadSegmentId = r.RoadSegmentId,
                TravelMinutes = r.TravelMinutes,
            }).ToList(),
        };
    }

    /// <summary>
    /// The danger level a task had when the latest plan was produced. Used only to decide
    /// whether danger has escalated for a replan; absence means "no prior baseline".
    /// </summary>
    private async Task<Dictionary<Guid, int>> BuildPreviousDangerMapAsync(CancellationToken ct)
    {
        // The current stored danger level is the baseline; the caller mutates task danger via
        // the tasks table before requesting a replan, so we compare against the last version's
        // recorded assignment set. We approximate the baseline as the danger levels captured at
        // the previous version. If none exists, no escalation is possible.
        var latest = await _db.AllocationVersions
            .AsNoTracking()
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(ct);
        if (latest is null) return new Dictionary<Guid, int>();

        return await _db.AllocationBaselines
            .AsNoTracking()
            .Where(b => b.AllocationVersionId == latest.Id)
            .ToDictionaryAsync(b => b.TaskId, b => b.DangerLevel, ct);
    }

    private async Task<int> NextVersionNumberAsync(CancellationToken ct)
    {
        var max = await _db.AllocationVersions
            .AsNoTracking()
            .Select(v => (int?)v.VersionNumber)
            .MaxAsync(ct);
        return (max ?? 0) + 1;
    }

    private AllocationVersion MapToVersion(SolveRequest request, SolveResult result, int versionNumber, SolveInput input)
    {
        var versionId = Guid.NewGuid();
        var version = new AllocationVersion
        {
            Id = versionId,
            VersionNumber = versionNumber,
            InputVersion = request.InputVersion,
            Kind = request.IsReplan ? AllocationKind.Replan : AllocationKind.Initial,
            TotalCostMinutes = result.TotalCostMinutes,
            HasUnassignedTasks = result.HasUnassignedTasks,
            CreatedAt = DateTimeOffset.UtcNow,
            Assignments = result.Assignments.Select(a => new Assignment
            {
                Id = Guid.NewGuid(),
                AllocationVersionId = versionId,
                TaskId = a.TaskId,
                TeamId = a.TeamId,
                VehicleId = a.VehicleId,
                RoadSegmentId = a.RoadSegmentId,
                ArrivalMinutes = a.ArrivalMinutes,
                TaskCode = a.TaskCode,
                TeamCode = a.TeamCode,
                VehicleCode = a.VehicleCode,
                RoadSegmentCode = a.RoadSegmentCode,
            }).ToList(),
            UnassignedReasons = result.Unassigned.Select(u => new UnassignedReason
            {
                Id = Guid.NewGuid(),
                AllocationVersionId = versionId,
                TaskId = u.TaskId,
                TaskCode = u.TaskCode,
                Code = u.Code,
                Detail = u.Detail,
            }).ToList(),
            AuditEntries = result.Audit.Select(a => new AuditEntry
            {
                Id = Guid.NewGuid(),
                AllocationVersionId = versionId,
                Sequence = a.Sequence,
                TaskId = a.TaskId,
                TaskCode = a.TaskCode,
                RuleCode = a.RuleCode,
                Message = a.Message,
            }).ToList(),
            Baselines = input.Tasks.Select(t => new AllocationBaseline
            {
                Id = Guid.NewGuid(),
                AllocationVersionId = versionId,
                TaskId = t.Id,
                DangerLevel = t.DangerLevel,
            }).ToList(),
        };
        return version;
    }

    public async Task<AllocationVersion?> GetVersionAsync(int versionNumber, CancellationToken ct = default) =>
        await _db.AllocationVersions
            .AsNoTracking()
            .Include(v => v.Assignments)
            .Include(v => v.UnassignedReasons)
            .Include(v => v.AuditEntries)
            .FirstOrDefaultAsync(v => v.VersionNumber == versionNumber, ct);

    public async Task<AllocationVersion?> GetByInputVersionAsync(string inputVersion, CancellationToken ct = default) =>
        await _db.AllocationVersions
            .AsNoTracking()
            .Include(v => v.Assignments)
            .Include(v => v.UnassignedReasons)
            .Include(v => v.AuditEntries)
            .FirstOrDefaultAsync(v => v.InputVersion == inputVersion, ct);

    public async Task<AllocationVersion?> GetLatestAsync(CancellationToken ct = default) =>
        await _db.AllocationVersions
            .AsNoTracking()
            .Include(v => v.Assignments)
            .Include(v => v.UnassignedReasons)
            .Include(v => v.AuditEntries)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(ct);

    public async Task<AllocationExplanation?> ExplainAsync(int versionNumber, CancellationToken ct = default)
    {
        var version = await GetVersionAsync(versionNumber, ct);
        if (version is null) return null;

        return new AllocationExplanation
        {
            VersionNumber = version.VersionNumber,
            InputVersion = version.InputVersion,
            Kind = version.Kind,
            TotalCostMinutes = version.TotalCostMinutes,
            Audit = version.AuditEntries.OrderBy(a => a.Sequence).ToList(),
            Assignments = version.Assignments.OrderBy(a => a.TaskCode, StringComparer.Ordinal).ToList(),
            Unassigned = version.UnassignedReasons.OrderBy(u => u.TaskCode, StringComparer.Ordinal).ToList(),
        };
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        // Npgsql surfaces SQLSTATE 23505; SQLite surfaces "UNIQUE constraint failed".
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("23505", StringComparison.OrdinalIgnoreCase)
            || message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransient(Exception ex)
    {
        // Serialization failures (SQLSTATE 40001) and deadlocks (40P01) are safe to retry on
        // PostgreSQL; SQLite surfaces write contention as "database is locked"/"busy".
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("40001", StringComparison.OrdinalIgnoreCase)
            || message.Contains("40P01", StringComparison.OrdinalIgnoreCase)
            || message.Contains("could not serialize", StringComparison.OrdinalIgnoreCase)
            || message.Contains("deadlock", StringComparison.OrdinalIgnoreCase)
            || message.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
            || message.Contains("database table is locked", StringComparison.OrdinalIgnoreCase)
            || message.Contains("busy", StringComparison.OrdinalIgnoreCase);
    }
}
