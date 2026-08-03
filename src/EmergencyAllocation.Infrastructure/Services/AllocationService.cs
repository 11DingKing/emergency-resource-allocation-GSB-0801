using System.Collections.Concurrent;
using EmergencyAllocation.Domain;
using EmergencyAllocation.Domain.Dtos;
using EmergencyAllocation.Domain.Services;
using EmergencyAllocation.Domain.Solver;
using EmergencyAllocation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TaskStatus = EmergencyAllocation.Domain.TaskStatus;

namespace EmergencyAllocation.Infrastructure.Services;

public sealed class AllocationService : IAllocationService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> KeyedLocks = new();

    private readonly IDbContextFactory<AllocationDbContext> _contextFactory;
    private readonly IAllocationSolver _solver;
    private readonly ISnapshotDigestService _digests;
    private readonly ILogger<AllocationService> _logger;

    public AllocationService(
        IDbContextFactory<AllocationDbContext> contextFactory,
        IAllocationSolver solver,
        ISnapshotDigestService digests,
        ILogger<AllocationService> logger)
    {
        _contextFactory = contextFactory;
        _solver = solver;
        _digests = digests;
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
        var payloadDigest = _digests.ComputePayloadDigest(
            operation, request.InputVersion, request.Reason, request.RoadEventId);

        await using var strategyCtx = await _contextFactory.CreateDbContextAsync(ct);
        var strategy = strategyCtx.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
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

                var (teams, vehicles, tasks, roads, currentDigests) = await LoadStateAsync(ctx, ct);

                ValidateExpectedDigests(request, currentDigests);

                if (existing is not null && IsTerminal(existing.Status))
                {
                    var fieldDiffs = new List<FieldDiffDto>();
                    if (!string.Equals(existing.RequestPayloadDigest, payloadDigest, StringComparison.OrdinalIgnoreCase))
                    {
                        fieldDiffs.Add(new FieldDiffDto("requestPayloadDigest",
                            existing.RequestPayloadDigest, payloadDigest,
                            "The request payload differs from the original call that produced this version."));
                    }
                    if (!string.IsNullOrEmpty(existing.RoadDigest)
                        && !string.Equals(existing.RoadDigest, currentDigests.Road, StringComparison.OrdinalIgnoreCase))
                    {
                        fieldDiffs.Add(new FieldDiffDto("roadDigest",
                            existing.RoadDigest, currentDigests.Road,
                            "Road snapshot has changed since the version was produced."));
                    }
                    if (!string.IsNullOrEmpty(existing.TeamDigest)
                        && !string.Equals(existing.TeamDigest, currentDigests.Team, StringComparison.OrdinalIgnoreCase))
                    {
                        fieldDiffs.Add(new FieldDiffDto("teamDigest",
                            existing.TeamDigest, currentDigests.Team,
                            "Team snapshot has changed since the version was produced."));
                    }
                    if (!string.IsNullOrEmpty(existing.VehicleDigest)
                        && !string.Equals(existing.VehicleDigest, currentDigests.Vehicle, StringComparison.OrdinalIgnoreCase))
                    {
                        fieldDiffs.Add(new FieldDiffDto("vehicleDigest",
                            existing.VehicleDigest, currentDigests.Vehicle,
                            "Vehicle snapshot has changed since the version was produced."));
                    }

                    if (fieldDiffs.Count > 0)
                    {
                        await tx.RollbackAsync(ct);
                        throw new SnapshotConflictException(new SnapshotConflictDto(
                            idempotencyKey,
                            existing.Status.ToString(),
                            existing.RequestPayloadDigest ?? string.Empty,
                            payloadDigest,
                            fieldDiffs,
                            $"Idempotency key {idempotencyKey} was already used against a different snapshot."));
                    }

                    await tx.RollbackAsync(ct);
                    _logger.LogInformation(
                        "Idempotent replay on {Operation} key={Key} version={VersionId}",
                        operation, idempotencyKey, existing.Id);
                    return await MapVersionAsync(existing.Id, ct);
                }

                if (existing is not null && existing.Status == AllocationVersionStatus.Pending)
                {
                    ctx.AllocationVersions.Remove(existing);
                    await ctx.SaveChangesAsync(ct);
                }

                var roadSnapshotVersion = currentDigests.RoadSnapshotVersion;
                var solverRequest = new SolverRequest(
                    ToSolverTeams(teams),
                    ToSolverTasks(tasks),
                    ToSolverRoads(roads),
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
                        RoadDigest = currentDigests.Road,
                        TaskDigest = currentDigests.Task,
                        TeamDigest = currentDigests.Team,
                        VehicleDigest = currentDigests.Vehicle,
                        RequestPayloadDigest = payloadDigest,
                        TriggeringRoadEventId = request.RoadEventId,
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
                    RoadDigest = currentDigests.Road,
                    TaskDigest = currentDigests.Task,
                    TeamDigest = currentDigests.Team,
                    VehicleDigest = currentDigests.Vehicle,
                    RequestPayloadDigest = payloadDigest,
                    TriggeringRoadEventId = request.RoadEventId,
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
                        RoadEventId = request.RoadEventId,
                        Message = au.Message
                    });
                }

                if (!string.IsNullOrEmpty(request.RoadEventId))
                {
                    version.AuditEntries.Add(new AllocationAuditEntry
                    {
                        Id = Guid.NewGuid(),
                        Order = version.AuditEntries.Count + 1,
                        Kind = "road-event",
                        RoadCode = null,
                        RoadEventId = request.RoadEventId,
                        Message = $"Solve request explicitly references road event {request.RoadEventId}."
                    });
                }

                ctx.AllocationVersions.Add(version);

                if (result.Feasible)
                {
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
            catch (Exception) when (ctx.Database.CurrentTransaction is not null)
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        });
    }

    private void ValidateExpectedDigests(SolveRequestDto request, SnapshotDigests current)
    {
        var diffs = new List<FieldDiffDto>();
        void Check(string field, string? expected, string actual)
        {
            if (!string.IsNullOrWhiteSpace(expected) &&
                !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                diffs.Add(new FieldDiffDto(field, expected, actual,
                    $"Client expected {field}={expected} but current snapshot is {actual}."));
            }
        }

        Check("roadDigest", request.ExpectedRoadDigest, current.Road);
        Check("taskDigest", request.ExpectedTaskDigest, current.Task);
        Check("teamDigest", request.ExpectedTeamDigest, current.Team);
        Check("vehicleDigest", request.ExpectedVehicleDigest, current.Vehicle);
        if (request.ExpectedRoadSnapshotVersion.HasValue
            && request.ExpectedRoadSnapshotVersion.Value != current.RoadSnapshotVersion)
        {
            diffs.Add(new FieldDiffDto("roadSnapshotVersion",
                request.ExpectedRoadSnapshotVersion.Value.ToString(),
                current.RoadSnapshotVersion.ToString(),
                "Road snapshot version differs from the value asserted by the caller."));
        }

        if (diffs.Count > 0)
        {
            throw new SnapshotConflictException(new SnapshotConflictDto(
                string.Empty, "Current", string.Empty,
                _digests.ComputePayloadDigest("expected", request.InputVersion, request.Reason, request.RoadEventId),
                diffs,
                "Request's expected digests do not match the current server snapshot."));
        }
    }

    private static List<FieldDiffDto> BuildConflictDiffs(
        AllocationVersion existing,
        SnapshotDigests current,
        string currentPayloadDigest,
        SolveRequestDto request)
    {
        var diffs = new List<FieldDiffDto>();
        void Add(string field, string? expected, string? actual, string detail)
        {
            if (!string.Equals(expected ?? string.Empty, actual ?? string.Empty, StringComparison.Ordinal))
                diffs.Add(new FieldDiffDto(field, expected, actual, detail));
        }

        Add("requestPayloadDigest", existing.RequestPayloadDigest, currentPayloadDigest,
            "The request payload differs from the original call that produced this version.");
        Add("roadDigest", existing.RoadDigest, current.Road,
            "Road snapshot has changed since the version was produced.");
        Add("taskDigest", existing.TaskDigest, current.Task,
            "Task snapshot has changed since the version was produced.");
        Add("teamDigest", existing.TeamDigest, current.Team,
            "Team snapshot has changed since the version was produced.");
        Add("vehicleDigest", existing.VehicleDigest, current.Vehicle,
            "Vehicle snapshot has changed since the version was produced.");

        return diffs;
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

    public Task<RoadEventDto> InterruptRoadAsync(RoadInterruptRequestDto request, CancellationToken ct = default)
        => ChangeRoadAsync(request.RoadCode, false, request.Reason ?? "Road interrupted",
            request.InputVersion, request.EventId ?? Guid.NewGuid().ToString("N"), request.RecordedBy, ct);

    public Task<RoadEventDto> ReopenRoadAsync(RoadReopenRequestDto request, CancellationToken ct = default)
        => ChangeRoadAsync(request.RoadCode, true, request.Reason ?? "Road reopened",
            request.InputVersion, request.EventId ?? Guid.NewGuid().ToString("N"), request.RecordedBy, ct);

    private async Task<RoadEventDto> ChangeRoadAsync(
        string roadCode, bool isOpen, string reason, string inputVersion,
        string eventId, string? recordedBy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(inputVersion))
            throw new ArgumentException("InputVersion is required.", nameof(inputVersion));

        await using var strategyCtx = await _contextFactory.CreateDbContextAsync(ct);
        var strategy = strategyCtx.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
            await using var tx = await ctx.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, ct);

            try
            {
                var duplicate = await ctx.RoadEvents.FirstOrDefaultAsync(e => e.EventId == eventId, ct);
                if (duplicate is not null)
                {
                    await tx.RollbackAsync(ct);
                    return ToDto(duplicate);
                }

                var road = await ctx.RoadSegments.FirstOrDefaultAsync(r => r.Code == roadCode, ct);
                if (road is null)
                    throw new KeyNotFoundException($"Road {roadCode} not found.");

                var before = road.RoadSnapshotVersion;
                road.IsOpen = isOpen;
                road.InterruptionReason = isOpen ? null : reason;
                road.UpdatedAt = DateTimeOffset.UtcNow;

                var nextVersion = (await ctx.RoadSegments.MaxAsync(r => (long?)r.RoadSnapshotVersion, ct) ?? 0L) + 1;
                road.RoadSnapshotVersion = nextVersion;

                var evt = new RoadEvent
                {
                    Id = Guid.NewGuid(),
                    EventId = eventId,
                    RoadCode = roadCode,
                    Kind = isOpen ? RoadEventKind.Reopen : RoadEventKind.Interruption,
                    Reason = reason,
                    RoadSnapshotVersionBefore = before,
                    RoadSnapshotVersionAfter = nextVersion,
                    OccurredAt = DateTimeOffset.UtcNow,
                    RecordedBy = recordedBy
                };
                ctx.RoadEvents.Add(evt);

                await ctx.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return ToDto(evt);
            }
            catch (Exception) when (ctx.Database.CurrentTransaction is not null)
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        });
    }

    public async Task<TaskDto> EscalateTaskAsync(TaskEscalationRequestDto request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.InputVersion))
            throw new ArgumentException("InputVersion is required.", nameof(request));
        if (!Enum.TryParse<TaskSeverity>(request.TargetSeverity, ignoreCase: true, out var target))
            throw new ArgumentException($"Unknown severity {request.TargetSeverity}", nameof(request));

        await using var strategyCtx = await _contextFactory.CreateDbContextAsync(ct);
        var strategy = strategyCtx.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
            await using var tx = await ctx.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, ct);
            try
            {
                var task = await ctx.Tasks.Include(t => t.RequiredCapabilities)
                    .FirstOrDefaultAsync(t => t.Code == request.TaskCode, ct);
                if (task is null) throw new KeyNotFoundException($"Task {request.TaskCode} not found.");

                task.Severity = target;
                task.SeverityVersion++;

                await ctx.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return new TaskDto(
                    task.Id, task.Code, task.Title, task.LocationNodeId,
                    task.Severity.ToString(), task.Status.ToString(),
                    task.DurationMinutes, task.DeadlineMinutes,
                    task.SeverityVersion, task.AssignedTeamId, task.AssignedVehicleId,
                    task.StartedAt,
                    task.RequiredCapabilities.Select(c => c.Capability).OrderBy(x => x).ToList());
            }
            catch (Exception) when (ctx.Database.CurrentTransaction is not null)
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        });
    }

    public async Task<TaskDto> StartTaskAsync(TaskStartRequestDto request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.InputVersion))
            throw new ArgumentException("InputVersion is required.", nameof(request));

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var task = await ctx.Tasks.Include(t => t.RequiredCapabilities)
            .FirstOrDefaultAsync(t => t.Code == request.TaskCode, ct);
        if (task is null) throw new KeyNotFoundException($"Task {request.TaskCode} not found.");

        task.Status = TaskStatus.InProgress;
        task.StartedAt ??= DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync(ct);

        return ToTaskDto(task);
    }

    public async Task<TaskDto> CompleteTaskAsync(TaskCompleteRequestDto request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.InputVersion))
            throw new ArgumentException("InputVersion is required.", nameof(request));

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var task = await ctx.Tasks.Include(t => t.RequiredCapabilities)
            .FirstOrDefaultAsync(t => t.Code == request.TaskCode, ct);
        if (task is null) throw new KeyNotFoundException($"Task {request.TaskCode} not found.");

        task.Status = TaskStatus.Completed;
        task.CompletedAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync(ct);

        return ToTaskDto(task);
    }

    public async Task<AllocationVersionDiffDto> DiffVersionsAsync(Guid fromVersionId, Guid toVersionId, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var from = await ctx.AllocationVersions.AsNoTracking()
            .Include(v => v.Assignments).ThenInclude(a => a.Task)
            .Include(v => v.Assignments).ThenInclude(a => a.Team)
            .Include(v => v.Assignments).ThenInclude(a => a.Vehicle)
            .FirstOrDefaultAsync(v => v.Id == fromVersionId, ct);
        var to = await ctx.AllocationVersions.AsNoTracking()
            .Include(v => v.Assignments).ThenInclude(a => a.Task)
            .Include(v => v.Assignments).ThenInclude(a => a.Team)
            .Include(v => v.Assignments).ThenInclude(a => a.Vehicle)
            .FirstOrDefaultAsync(v => v.Id == toVersionId, ct);
        if (from is null || to is null) throw new KeyNotFoundException("One or both versions not found.");

        var fromAssignments = from.Assignments.ToDictionary(a => a.TaskId);
        var toAssignments = to.Assignments.ToDictionary(a => a.TaskId);
        var taskCodes = from.Assignments.Select(a => a.Task?.Code ?? a.TaskId.ToString())
            .Concat(to.Assignments.Select(a => a.Task?.Code ?? a.TaskId.ToString()))
            .Distinct(StringComparer.Ordinal).ToList();

        var assignmentDiffs = new List<AssignmentDiffDto>();
        foreach (var code in taskCodes.OrderBy(x => x, StringComparer.Ordinal))
        {
            var fa = from.Assignments.FirstOrDefault(a => (a.Task?.Code ?? a.TaskId.ToString()) == code);
            var ta = to.Assignments.FirstOrDefault(a => (a.Task?.Code ?? a.TaskId.ToString()) == code);
            if (fa is null || ta is null) continue;

            var changed = fa.Decision != ta.Decision
                          || fa.TeamId != ta.TeamId
                          || fa.VehicleId != ta.VehicleId
                          || fa.EstimatedTravelMinutes != ta.EstimatedTravelMinutes;
            if (!changed) continue;

            assignmentDiffs.Add(new AssignmentDiffDto(
                code,
                fa.Decision.ToString(), ta.Decision.ToString(),
                fa.Team?.Code, ta.Team?.Code,
                fa.Vehicle?.Code, ta.Vehicle?.Code,
                fa.EstimatedTravelMinutes, ta.EstimatedTravelMinutes,
                ta.Preempted,
                ta.Reason));
        }

        var snapshotDiffs = new List<FieldDiffDto>();
        void AddDiff(string field, string? expected, string? actual, string detail)
        {
            if (!string.Equals(expected ?? string.Empty, actual ?? string.Empty, StringComparison.Ordinal))
                snapshotDiffs.Add(new FieldDiffDto(field, expected, actual, detail));
        }
        AddDiff("roadDigest", from.RoadDigest, to.RoadDigest, "Road snapshot changed between versions.");
        AddDiff("taskDigest", from.TaskDigest, to.TaskDigest, "Task snapshot changed between versions.");
        AddDiff("teamDigest", from.TeamDigest, to.TeamDigest, "Team snapshot changed between versions.");
        AddDiff("vehicleDigest", from.VehicleDigest, to.VehicleDigest, "Vehicle snapshot changed between versions.");

        var summary = $"From {from.Status} (snapshot {from.RoadSnapshotVersion}) to {to.Status} (snapshot {to.RoadSnapshotVersion}); " +
                      $"{assignmentDiffs.Count} assignment difference(s), {snapshotDiffs.Count} snapshot digest difference(s).";

        return new AllocationVersionDiffDto(
            from.Id, to.Id, from.Status.ToString(), to.Status.ToString(),
            from.RoadSnapshotVersion, to.RoadSnapshotVersion,
            from.RoadDigest, to.RoadDigest,
            assignmentDiffs, snapshotDiffs, summary);
    }

    private static TaskDto ToTaskDto(EmergencyTask task) => new(
        task.Id, task.Code, task.Title, task.LocationNodeId,
        task.Severity.ToString(), task.Status.ToString(),
        task.DurationMinutes, task.DeadlineMinutes,
        task.SeverityVersion, task.AssignedTeamId, task.AssignedVehicleId,
        task.StartedAt,
        task.RequiredCapabilities.Select(c => c.Capability).OrderBy(x => x).ToList());

    public async Task<SnapshotDigestDto> GetCurrentDigestsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var (_, _, _, _, digests) = await LoadStateAsync(ctx, ct);
        return new SnapshotDigestDto(digests.RoadSnapshotVersion,
            digests.Road, digests.Task, digests.Team, digests.Vehicle);
    }

    private async Task<(
        IReadOnlyList<Team> Teams,
        IReadOnlyList<Vehicle> Vehicles,
        IReadOnlyList<EmergencyTask> Tasks,
        IReadOnlyList<RoadSegment> Roads,
        SnapshotDigests Digests)>
        LoadStateAsync(AllocationDbContext ctx, CancellationToken ct)
    {
        var teams = await ctx.Teams.AsNoTracking().Include(t => t.Capabilities).Include(t => t.Vehicles).ToListAsync(ct);
        var vehicles = teams.SelectMany(t => t.Vehicles).ToList();
        var tasks = await ctx.Tasks.AsNoTracking().Include(t => t.RequiredCapabilities).ToListAsync(ct);
        var allRoads = await ctx.RoadSegments.AsNoTracking().ToListAsync(ct);

        var latestRoads = allRoads
            .GroupBy(r => r.Code)
            .Select(g => g.OrderByDescending(r => r.RoadSnapshotVersion).First())
            .ToList();

        var digests = _digests.Compute(teams, tasks, latestRoads, vehicles);
        return (teams, vehicles, tasks, latestRoads, digests);
    }

    private static IReadOnlyList<SolverTeam> ToSolverTeams(IEnumerable<Team> teams) => teams
        .Select(t => new SolverTeam(
            t.Id, t.Code, t.BaseNodeId, t.IsAvailable,
            t.Capabilities.Select(c => c.Capability).ToHashSet(StringComparer.Ordinal),
            t.Vehicles.Select(v => new SolverVehicle(
                v.Id, v.Code, v.TeamId, v.HeightMeters, v.AverageSpeedMetersPerMinute, v.IsAvailable)).ToList()))
        .ToList();

    private static IReadOnlyList<SolverTask> ToSolverTasks(IEnumerable<EmergencyTask> tasks) => tasks
        .Select(t => new SolverTask(
            t.Id, t.Code, t.Title, t.LocationNodeId, t.Severity, t.SeverityVersion, t.Status,
            t.DurationMinutes, t.DeadlineMinutes,
            t.RequiredCapabilities.Select(r => r.Capability).ToHashSet(StringComparer.Ordinal),
            t.AssignedTeamId, t.AssignedVehicleId,
            t.Status == TaskStatus.InProgress && t.StartedAt.HasValue))
        .ToList();

    private static IReadOnlyList<SolverRoad> ToSolverRoads(IEnumerable<RoadSegment> roads) => roads
        .Select(r => new SolverRoad(
            r.Code, r.FromNodeId, r.ToNodeId, r.TravelTimeMinutes,
            r.HeightLimitMeters, r.IsOpen, r.RoadSnapshotVersion))
        .ToList();

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

        SnapshotDigestDto? current = null;
        try
        {
            var (teams, vehicles, tasks, roads, digests) = await LoadStateAsync(ctx, ct);
            current = new SnapshotDigestDto(digests.RoadSnapshotVersion,
                digests.Road, digests.Task, digests.Team, digests.Vehicle);
        }
        catch
        {
            current = null;
        }

        return ToDto(v, current);
    }

    private static AllocationVersionDto ToDto(AllocationVersion v, SnapshotDigestDto? current = null) => new(
        v.Id,
        v.Operation,
        v.InputVersion,
        v.IdempotencyKey,
        v.Status.ToString(),
        v.Sequence,
        v.CreatedAt,
        v.CommittedAt,
        v.RoadSnapshotVersion,
        v.RoadDigest,
        v.TaskDigest,
        v.TeamDigest,
        v.VehicleDigest,
        v.RequestPayloadDigest,
        v.TriggeringRoadEventId,
        v.TotalCostMinutes,
        v.AssignedCount,
        v.UnassignedCount,
        v.FailureReason,
        current,
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
            a.Order, a.Kind, a.TaskCode, a.TeamCode, a.VehicleCode, a.RoadCode, a.RoadEventId, a.Message)).ToList());

    private static RoadEventDto ToDto(RoadEvent e) => new(
        e.Id, e.EventId, e.RoadCode, e.Kind.ToString(), e.Reason,
        e.RoadSnapshotVersionBefore, e.RoadSnapshotVersionAfter,
        e.OccurredAt, e.RecordedBy);
}
