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
            request.ExpectedRoadSnapshotHash,
            request.ExpectedTaskSnapshotHash,
            request.ExpectedTeamSnapshotHash,
            request.ExpectedVehicleSnapshotHash,
            request.TriggeringRoadEventId,
            cancellationToken);
    }

    public Task<AllocationResult> RearrangeAsync(RearrangeRequest request, CancellationToken cancellationToken = default)
    {
        return SolveWithIdempotencyAsync(
            request.InputVersion,
            previousVersionId: request.PreviousVersionId,
            dangerLevelRaised: request.DangerLevelRaised,
            allowPreemption: true,
            request.ExpectedRoadSnapshotHash,
            request.ExpectedTaskSnapshotHash,
            request.ExpectedTeamSnapshotHash,
            request.ExpectedVehicleSnapshotHash,
            request.TriggeringRoadEventId,
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

    public async Task<VersionDiffDto?> GetDiffAsync(Guid toVersionId, Guid fromVersionId, CancellationToken cancellationToken = default)
    {
        var to = await GetVersionAsync(toVersionId, cancellationToken);
        var from = await GetVersionAsync(fromVersionId, cancellationToken);
        if (to is null || from is null)
        {
            return null;
        }

        var assignmentChanges = BuildAssignmentChanges(from, to);
        var snapshotChanges = BuildSnapshotChanges(from, to);
        var reasons = BuildChangeReasons(from, to, assignmentChanges);

        return new VersionDiffDto(
            from.VersionId,
            from.InputVersion,
            to.VersionId,
            to.InputVersion,
            from.TotalCost,
            to.TotalCost,
            to.TotalCost - from.TotalCost,
            assignmentChanges,
            snapshotChanges,
            to.TriggeringRoadEventId,
            reasons);
    }

    private static IReadOnlyList<AssignmentChangeDto> BuildAssignmentChanges(AllocationResult from, AllocationResult to)
    {
        var changes = new List<AssignmentChangeDto>();
        var fromByTask = from.Assignments.ToDictionary(a => a.TaskId);
        var toByTask = to.Assignments.ToDictionary(a => a.TaskId);
        var allTaskIds = fromByTask.Keys.Concat(toByTask.Keys).Distinct().OrderBy(t => t, StringComparer.Ordinal);

        foreach (var taskId in allTaskIds)
        {
            fromByTask.TryGetValue(taskId, out var f);
            toByTask.TryGetValue(taskId, out var t);

            var fromTeam = f?.TeamId;
            var toTeam = t?.TeamId;
            var teamChanged = !string.Equals(fromTeam, toTeam, StringComparison.Ordinal);
            var fromRoute = f?.RouteNodes;
            var toRoute = t?.RouteNodes;
            var routeChanged = !(fromRoute is not null && toRoute is not null && fromRoute.SequenceEqual(toRoute));
            var arrivalChanged = (f?.EstimatedArrivalMinutes ?? 0) != (t?.EstimatedArrivalMinutes ?? 0);
            var kindChanged = !string.Equals(f?.Kind, t?.Kind, StringComparison.Ordinal);

            var changeType = (f, t) switch
            {
                (null, not null) => "added",
                (not null, null) => "removed",
                _ when teamChanged => "team_changed",
                _ when routeChanged || arrivalChanged => "route_changed",
                _ when kindChanged => "kind_changed",
                _ => "unchanged"
            };

            if (changeType == "unchanged")
            {
                continue;
            }

            changes.Add(new AssignmentChangeDto(
                taskId,
                fromTeam,
                toTeam,
                fromRoute,
                toRoute,
                f?.EstimatedArrivalMinutes ?? 0,
                t?.EstimatedArrivalMinutes ?? 0,
                f?.Kind ?? "Unassigned",
                t?.Kind ?? "Unassigned",
                changeType,
                t?.PreemptionReason));
        }

        return changes;
    }

    private static IReadOnlyList<SnapshotFieldChangeDto> BuildSnapshotChanges(AllocationResult from, AllocationResult to)
    {
        var fields = new (string Field, string? From, string? To)[]
        {
            ("roads", from.SnapshotHashes.Roads, to.SnapshotHashes.Roads),
            ("tasks", from.SnapshotHashes.Tasks, to.SnapshotHashes.Tasks),
            ("teams", from.SnapshotHashes.Teams, to.SnapshotHashes.Teams),
            ("vehicles", from.SnapshotHashes.Vehicles, to.SnapshotHashes.Vehicles)
        };

        return fields
            .Select(f => new SnapshotFieldChangeDto(f.Field, f.From, f.To,
                !string.Equals(f.From, f.To, StringComparison.Ordinal)))
            .ToList();
    }

    private static IReadOnlyList<string> BuildChangeReasons(
        AllocationResult from,
        AllocationResult to,
        IReadOnlyList<AssignmentChangeDto> changes)
    {
        var reasons = new List<string>();

        if (!string.IsNullOrEmpty(to.TriggeringRoadEventId) &&
            !string.Equals(from.TriggeringRoadEventId, to.TriggeringRoadEventId, StringComparison.Ordinal))
        {
            reasons.Add($"触发道路事件: {to.TriggeringRoadEventId}");
        }

        var roadChanged = from.SnapshotHashes.Roads != to.SnapshotHashes.Roads;
        if (roadChanged)
        {
            reasons.Add("道路快照发生变化（路段中断或恢复）");
        }

        var taskChanged = from.SnapshotHashes.Tasks != to.SnapshotHashes.Tasks;
        if (taskChanged)
        {
            reasons.Add("任务快照发生变化（状态、危险等级或指派变更）");
        }

        foreach (var change in changes)
        {
            if (change.ChangeType == "team_changed" && change.PreemptionReason is not null)
            {
                reasons.Add($"任务 {change.TaskId}: {change.FromTeamId} → {change.ToTeamId}（{change.PreemptionReason}）");
            }
            else if (change.ChangeType == "team_changed")
            {
                reasons.Add($"任务 {change.TaskId}: 队伍由 {change.FromTeamId ?? "未分配"} 变更为 {change.ToTeamId ?? "未分配"}");
            }
            else if (change.ChangeType == "route_changed" && change.ToArrivalMinutes < change.FromArrivalMinutes)
            {
                reasons.Add($"任务 {change.TaskId}: 到达时间由 {change.FromArrivalMinutes} 分钟缩短至 {change.ToArrivalMinutes} 分钟（已执行任务未因 ETA 变短而移动）");
            }
            else if (change.ChangeType == "route_changed")
            {
                reasons.Add($"任务 {change.TaskId}: 路线由 {string.Join("→", change.FromRoute ?? Array.Empty<string>())} 变为 {string.Join("→", change.ToRoute ?? Array.Empty<string>())}");
            }
        }

        if (to.TotalCost != from.TotalCost)
        {
            reasons.Add($"总成本变化: {from.TotalCost} → {to.TotalCost}（Δ {to.TotalCost - from.TotalCost}）");
        }

        return reasons;
    }

    private async Task<AllocationResult> SolveWithIdempotencyAsync(
        string inputVersion,
        Guid? previousVersionId,
        bool dangerLevelRaised,
        bool allowPreemption,
        string? expectedRoadHash,
        string? expectedTaskHash,
        string? expectedTeamHash,
        string? expectedVehicleHash,
        string? triggeringRoadEventId,
        CancellationToken cancellationToken)
    {
        var gate = VersionLocks.GetOrAdd(inputVersion, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt < MaxRetries; attempt++)
            {
                try
                {
                    var snapshot = await LoadSnapshotAsync(cancellationToken);
                    var options = new SolverOptions(dangerLevelRaised, allowPreemption, _solver.Version);
                    var problem = SnapshotBuilder.Build(
                        snapshot.Teams, snapshot.Tasks, snapshot.Roads, snapshot.RoadEvents,
                        options, previousVersionId, inputVersion, triggeringRoadEventId);
                    var hashes = SnapshotBuilder.ComputeHashes(problem);

                    ValidateExpectedHashes(inputVersion, expectedRoadHash, expectedTaskHash, expectedTeamHash,
                        expectedVehicleHash, hashes);

                    var existing = await GetByInputVersionAsync(inputVersion, cancellationToken);
                    if (existing is not null)
                    {
                        if (existing.SnapshotHash == hashes.Combined)
                        {
                            _logger.LogInformation("Input version {InputVersion} already committed with identical snapshot; replaying {VersionId}.",
                                inputVersion, existing.VersionId);
                            return existing;
                        }

                        throw new SnapshotConflictException(BuildIdempotentConflict(inputVersion, existing, hashes, snapshot));
                    }

                    _logger.LogInformation("Solver attempt {Attempt} for {InputVersion} with snapshot {Hash}.",
                        attempt, inputVersion, hashes.Combined);

                    var solverResult = await _solver.SolveAsync(problem, cancellationToken);

                    var version = BuildVersionEntity(problem, solverResult, hashes);
                    await PersistVersionAsync(version, cancellationToken);
                    return Map(version);
                }
                catch (SnapshotConflictException)
                {
                    throw;
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex))
                {
                    _logger.LogWarning(ex, "Unique violation for {InputVersion}; reading winner.", inputVersion);
                    var winner = await GetByInputVersionAsync(inputVersion, cancellationToken);
                    if (winner is not null)
                    {
                        var currentSnapshot = await LoadSnapshotAsync(cancellationToken);
                        var currentProblem = SnapshotBuilder.Build(
                            currentSnapshot.Teams, currentSnapshot.Tasks, currentSnapshot.Roads,
                            currentSnapshot.RoadEvents,
                            new SolverOptions(dangerLevelRaised, allowPreemption, _solver.Version),
                            null, inputVersion, triggeringRoadEventId);
                        var currentHashes = SnapshotBuilder.ComputeHashes(currentProblem);
                        if (winner.SnapshotHash != currentHashes.Combined)
                        {
                            throw new SnapshotConflictException(BuildIdempotentConflict(
                                inputVersion, winner, currentHashes, currentSnapshot));
                        }

                        return winner;
                    }
                }
                catch (Exception ex) when (IsTransient(ex) && attempt < MaxRetries - 1)
                {
                    var delay = TimeSpan.FromMilliseconds(50 * Math.Pow(2, attempt));
                    _logger.LogWarning(ex, "Transient failure on attempt {Attempt} for {InputVersion}; retrying in {Delay}ms.",
                        attempt, inputVersion, delay.TotalMilliseconds);
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

    private static void ValidateExpectedHashes(
        string inputVersion,
        string? expectedRoadHash,
        string? expectedTaskHash,
        string? expectedTeamHash,
        string? expectedVehicleHash,
        SnapshotHashes hashes)
    {
        var diffs = new List<FieldDiffDto>();
        AddIfDiff(diffs, "roads", expectedRoadHash, hashes.Roads, "道路快照与请求绑定的版本不一致");
        AddIfDiff(diffs, "tasks", expectedTaskHash, hashes.Tasks, "任务快照与请求绑定的版本不一致");
        AddIfDiff(diffs, "teams", expectedTeamHash, hashes.Teams, "队伍快照与请求绑定的版本不一致");
        AddIfDiff(diffs, "vehicles", expectedVehicleHash, hashes.Vehicles, "车辆快照与请求绑定的版本不一致");

        if (diffs.Count > 0)
        {
            throw new SnapshotConflictException(new SnapshotConflictResponse(
                inputVersion,
                "EXPECTED_SNAPSHOT_MISMATCH",
                "请求绑定的快照版本与当前真实快照不一致，拒绝基于旧快照重放。",
                ToDto(hashes),
                null,
                ToDto(hashes),
                diffs));
        }
    }

    private static void AddIfDiff(List<FieldDiffDto> diffs, string field, string? expected, string actual, string message)
    {
        if (expected is not null && !string.Equals(expected, actual, StringComparison.Ordinal))
        {
            diffs.Add(new FieldDiffDto(field, expected, actual, message, Array.Empty<string>()));
        }
    }

    private static SnapshotConflictResponse BuildIdempotentConflict(
        string inputVersion,
        AllocationResult committed,
        SnapshotHashes currentHashes,
        SnapshotData snapshot)
    {
        var committedHashes = committed.SnapshotHashes;
        var diffs = new List<FieldDiffDto>();

        AddCommittedDiff(diffs, "roads", committedHashes.Roads, currentHashes.Roads, snapshot, "R");
        AddCommittedDiff(diffs, "tasks", committedHashes.Tasks, currentHashes.Tasks, snapshot, "T");
        AddCommittedDiff(diffs, "teams", committedHashes.Teams, currentHashes.Teams, snapshot, "A");
        AddCommittedDiff(diffs, "vehicles", committedHashes.Vehicles, currentHashes.Vehicles, snapshot, "V");

        return new SnapshotConflictResponse(
            inputVersion,
            "INPUT_VERSION_SNAPSHOT_CONFLICT",
            $"相同 inputVersion '{inputVersion}' 已提交但其绑定快照与当前快照不同，拒绝重放旧方案；请使用新的 inputVersion 重排。",
            ToDto(currentHashes),
            committedHashes,
            ToDto(currentHashes),
            diffs);
    }

    private static void AddCommittedDiff(
        List<FieldDiffDto> diffs,
        string field,
        string committedHash,
        string currentHash,
        SnapshotData snapshot,
        string kind)
    {
        if (string.Equals(committedHash, currentHash, StringComparison.Ordinal))
        {
            return;
        }

        var related = kind switch
        {
            "R" => snapshot.RoadEvents.Select(e => e.Id)
                .Concat(snapshot.Roads.Where(r => !r.IsOpen).Select(r => r.Id))
                .Distinct().ToList(),
            "T" => snapshot.Tasks.Select(t => t.Id).ToList(),
            "A" => snapshot.Teams.Select(t => t.Id).ToList(),
            "V" => snapshot.Teams.Select(t => t.VehicleId).Distinct().ToList(),
            _ => new List<string>()
        };

        diffs.Add(new FieldDiffDto(
            field,
            committedHash,
            currentHash,
            $"已提交版本的 {field} 快照与当前不同",
            related));
    }

    private async Task<SnapshotData> LoadSnapshotAsync(CancellationToken cancellationToken)
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
        var roadEvents = await context.RoadEvents
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return new SnapshotData(teams, tasks, roads, roadEvents);
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
        SnapshotHashes hashes)
    {
        var version = new AllocationVersion
        {
            Id = Guid.NewGuid(),
            InputVersion = problem.InputVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = AllocationStatus.Committed,
            DangerLevelRaised = problem.Options.DangerLevelRaised,
            SnapshotTakenAt = problem.SnapshotTakenAt,
            SnapshotHash = hashes.Combined,
            RoadSnapshotHash = hashes.Roads,
            TaskSnapshotHash = hashes.Tasks,
            TeamSnapshotHash = hashes.Teams,
            VehicleSnapshotHash = hashes.Vehicles,
            TriggeringRoadEventId = problem.TriggeringRoadEventId,
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

    private static SnapshotHashesDto ToDto(SnapshotHashes hashes) =>
        new(hashes.Combined, hashes.Roads, hashes.Tasks, hashes.Teams, hashes.Vehicles);

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
            new SnapshotHashesDto(
                version.SnapshotHash,
                version.RoadSnapshotHash,
                version.TaskSnapshotHash,
                version.TeamSnapshotHash,
                version.VehicleSnapshotHash),
            version.TriggeringRoadEventId,
            version.PreviousVersionId,
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
                or "08006"
                or "08001";
        }

        if (ex is TimeoutException or IOException)
        {
            return true;
        }

        return false;
    }

    private sealed record SnapshotData(
        IReadOnlyList<Team> Teams,
        IReadOnlyList<EmergencyTask> Tasks,
        IReadOnlyList<RoadSegment> Roads,
        IReadOnlyList<RoadEvent> RoadEvents);
}
