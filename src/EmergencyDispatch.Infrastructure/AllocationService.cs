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
        if (string.IsNullOrWhiteSpace(request.SnapshotVersion))
        {
            throw new ArgumentException(
                "SnapshotVersion is required; a solve must bind to a real snapshot digest (GET /api/snapshot).",
                nameof(request));
        }

        var snapshots = new SnapshotService(_db);
        var current = await snapshots.BuildAsync(ct);

        // Fast path: this input version already produced a plan. Whether it is a valid replay
        // or a conflict depends on the snapshot it was bound to.
        var existing = await GetByInputVersionAsync(request.InputVersion, ct);
        if (existing is not null)
        {
            return await ResolveExistingAsync(request, existing, current, ct);
        }

        // A fresh solve must bind to the *current* world; if the caller's snapshot is stale the
        // world already moved, so we refuse rather than solve against a mismatched view.
        if (!string.Equals(request.SnapshotVersion, current.Version, StringComparison.Ordinal))
        {
            return await StaleRequestConflictAsync(request, current, ct);
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return await SolveAndPersistOnceAsync(request, current, ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // A concurrent writer committed this input version first. Re-read and decide:
                // identical snapshot ⇒ replay; different snapshot ⇒ conflict. Idempotency and
                // the snapshot binding are honoured; nothing of ours was persisted.
                _logger.LogInformation(
                    "Concurrent submit for input version {InputVersion} lost the race; resolving against the committed version.",
                    request.InputVersion);
                _db.ChangeTracker.Clear();
                var winner = await GetByInputVersionAsync(request.InputVersion, ct);
                if (winner is not null)
                {
                    return await ResolveExistingAsync(request, winner, current, ct);
                }
                // Extremely unlikely: violation without a readable row. Retry.
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < MaxAttempts)
            {
                _logger.LogWarning(ex,
                    "Transient failure solving input version {InputVersion} (attempt {Attempt}); retrying.",
                    request.InputVersion, attempt);
                // Small linear backoff so a writer holding a lock can make progress before we retry.
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), ct);
            }

            // Clear tracked entities before the next attempt so retries start clean.
            _db.ChangeTracker.Clear();
        }

        throw new InvalidOperationException(
            $"Failed to persist allocation for input version '{request.InputVersion}' after {MaxAttempts} attempts.");
    }

    /// <summary>
    /// Decide the outcome for an input version that already has a stored plan: replay when the
    /// stored snapshot digest matches the request, otherwise a conflict with a field-level diff.
    /// </summary>
    private async Task<AllocationResult> ResolveExistingAsync(
        SolveRequest request, AllocationVersion existing, WorldSnapshot current, CancellationToken ct)
    {
        if (string.Equals(existing.SnapshotVersion, request.SnapshotVersion, StringComparison.Ordinal))
        {
            // Identical input version AND identical snapshot ⇒ the payload is truly identical;
            // replay the exact stored plan.
            return new AllocationResult { Version = existing, WasExisting = true };
        }

        // Same input version, different snapshot: never replay the old plan. Diff the snapshot
        // the stored plan was bound to against the current world so the caller sees what moved.
        var stored = DeserializeStored(existing);
        var diff = SnapshotService.Diff(stored, current);
        var roadEvents = await LoadRoadEventsAsync(ct);

        _logger.LogInformation(
            "Snapshot conflict for input version {InputVersion}: stored {Stored} vs requested {Requested}.",
            request.InputVersion, existing.SnapshotVersion, request.SnapshotVersion);

        return new AllocationResult
        {
            Conflict = new SnapshotConflict
            {
                InputVersion = request.InputVersion,
                RequestedSnapshotVersion = request.SnapshotVersion,
                StoredSnapshotVersion = existing.SnapshotVersion,
                Diff = diff,
                RoadEvents = roadEvents,
                Message =
                    $"Input version '{request.InputVersion}' is already bound to snapshot " +
                    $"'{existing.SnapshotVersion}'. The submitted snapshot '{request.SnapshotVersion}' differs; " +
                    "the existing plan is not replayed. See the field-level diff and road events.",
            },
        };
    }

    private async Task<AllocationResult> StaleRequestConflictAsync(
        SolveRequest request, WorldSnapshot current, CancellationToken ct)
    {
        var roadEvents = await LoadRoadEventsAsync(ct);
        _logger.LogInformation(
            "Stale snapshot for input version {InputVersion}: requested {Requested} but current is {Current}.",
            request.InputVersion, request.SnapshotVersion, current.Version);

        return new AllocationResult
        {
            Conflict = new SnapshotConflict
            {
                InputVersion = request.InputVersion,
                RequestedSnapshotVersion = request.SnapshotVersion,
                StoredSnapshotVersion = current.Version,
                Diff = new SnapshotDiff
                {
                    StoredVersion = request.SnapshotVersion,
                    CurrentVersion = current.Version,
                    Changes = Array.Empty<FieldChange>(),
                },
                RoadEvents = roadEvents,
                Message =
                    $"Requested snapshot '{request.SnapshotVersion}' is not the current world snapshot " +
                    $"'{current.Version}'. Re-read GET /api/snapshot and resubmit; the world moved before this solve.",
            },
        };
    }

    private static WorldSnapshot DeserializeStored(AllocationVersion version)
    {
        if (string.IsNullOrEmpty(version.SnapshotJson))
        {
            return new WorldSnapshot
            {
                Version = version.SnapshotVersion,
                Roads = Array.Empty<WorldSnapshot.RoadState>(),
                Tasks = Array.Empty<WorldSnapshot.TaskState>(),
                Teams = Array.Empty<WorldSnapshot.TeamState>(),
                Vehicles = Array.Empty<WorldSnapshot.VehicleState>(),
                Routes = Array.Empty<WorldSnapshot.RouteState>(),
            };
        }
        var snap = System.Text.Json.JsonSerializer.Deserialize<WorldSnapshot>(
            version.SnapshotJson, WorldSnapshot.CanonicalJson)!;
        return snap with { Version = version.SnapshotVersion };
    }

    private async Task<IReadOnlyList<RoadEventRef>> LoadRoadEventsAsync(CancellationToken ct)
    {
        var events = await _db.RoadEvents.AsNoTracking().ToListAsync(ct);
        return events
            .OrderBy(e => e.RecordedAt).ThenBy(e => e.EventId, StringComparer.Ordinal)
            .Select(e => new RoadEventRef
            {
                EventId = e.EventId,
                RoadCode = e.RoadCode,
                Closed = e.Closed,
                RecordedAt = e.RecordedAt,
            })
            .ToList();
    }

    private async Task<AllocationResult> SolveAndPersistOnceAsync(
        SolveRequest request, WorldSnapshot boundSnapshot, CancellationToken ct)
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
            return await ResolveExistingAsync(request, existing, boundSnapshot, ct);
        }

        // Build the pure solver snapshot from a consistent read, then solve outside the DB.
        var input = await BuildSnapshotAsync(request, ct);
        SolveResult result = _solver.Solve(input);

        var nextNumber = await NextVersionNumberAsync(ct);
        var version = MapToVersion(request, result, nextNumber, input, boundSnapshot);

        _db.AllocationVersions.Add(version);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Persisted allocation version {VersionNumber} for input version {InputVersion} bound to snapshot {SnapshotVersion} ({AssignCount} assignments, {UnassignedCount} unassigned).",
            version.VersionNumber, version.InputVersion, version.SnapshotVersion,
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
        var events = await _db.RoadEvents.AsNoTracking().ToListAsync(ct);

        // Latest road event per road, so the solver can cite it when a closed road forces a reroute.
        var lastEventByRoad = events
            .GroupBy(e => e.RoadCode, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(e => e.RecordedAt).ThenBy(e => e.EventId, StringComparer.Ordinal).First().EventId,
                StringComparer.Ordinal);

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
                LastEventId = lastEventByRoad.TryGetValue(r.Code, out var ev) ? ev : null,
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

    private AllocationVersion MapToVersion(
        SolveRequest request, SolveResult result, int versionNumber, SolveInput input, WorldSnapshot boundSnapshot)
    {
        var versionId = Guid.NewGuid();
        var version = new AllocationVersion
        {
            Id = versionId,
            VersionNumber = versionNumber,
            InputVersion = request.InputVersion,
            SnapshotVersion = boundSnapshot.Version,
            SnapshotJson = boundSnapshot.ToCanonicalJson(),
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

        var roadEvents = await LoadRoadEventsAsync(ct);

        return new AllocationExplanation
        {
            VersionNumber = version.VersionNumber,
            InputVersion = version.InputVersion,
            SnapshotVersion = version.SnapshotVersion,
            Kind = version.Kind,
            TotalCostMinutes = version.TotalCostMinutes,
            Audit = version.AuditEntries.OrderBy(a => a.Sequence).ToList(),
            Assignments = version.Assignments.OrderBy(a => a.TaskCode, StringComparer.Ordinal).ToList(),
            Unassigned = version.UnassignedReasons.OrderBy(u => u.TaskCode, StringComparer.Ordinal).ToList(),
            RoadEvents = roadEvents,
        };
    }

    public async Task<WorldSnapshot> GetCurrentSnapshotAsync(CancellationToken ct = default) =>
        await new SnapshotService(_db).BuildAsync(ct);

    public async Task<RoadEventResult> RecordRoadEventAsync(
        string eventId, string roadCode, bool closed, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("eventId is required.", nameof(eventId));
        if (string.IsNullOrWhiteSpace(roadCode)) throw new ArgumentException("roadCode is required.", nameof(roadCode));

        // Idempotent on eventId: recording the same event twice returns the first outcome.
        var existing = await _db.RoadEvents.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == eventId, ct);
        if (existing is not null)
        {
            var snap = await new SnapshotService(_db).BuildAsync(ct);
            return new RoadEventResult
            {
                EventId = existing.EventId,
                RoadCode = existing.RoadCode,
                Closed = existing.Closed,
                WasExisting = true,
                SnapshotVersion = snap.Version,
            };
        }

        var road = await _db.RoadSegments.FirstOrDefaultAsync(r => r.Code == roadCode, ct)
            ?? throw new InvalidOperationException($"Unknown road code '{roadCode}'.");

        // Record the event and apply it to the road state atomically.
        _db.RoadEvents.Add(new RoadEvent
        {
            Id = Guid.NewGuid(),
            EventId = eventId,
            RoadCode = roadCode,
            Closed = closed,
            RecordedAt = DateTimeOffset.UtcNow,
        });
        road.IsOpen = !closed;
        await _db.SaveChangesAsync(ct);

        var snapshot = await new SnapshotService(_db).BuildAsync(ct);
        return new RoadEventResult
        {
            EventId = eventId,
            RoadCode = roadCode,
            Closed = closed,
            WasExisting = false,
            SnapshotVersion = snapshot.Version,
        };
    }

    public async Task<bool> SetTaskDangerAsync(string taskCode, string dangerLevel, CancellationToken ct = default)
    {
        if (!DangerLevels.IsDefined(dangerLevel))
        {
            throw new ArgumentException(
                $"Unknown danger level '{dangerLevel}'. Expected one of: routine, elevated, high, critical.",
                nameof(dangerLevel));
        }

        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Code == taskCode, ct);
        if (task is null) return false;

        task.DangerLevel = DangerLevels.Rank(dangerLevel);
        await _db.SaveChangesAsync(ct);
        return true;
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
