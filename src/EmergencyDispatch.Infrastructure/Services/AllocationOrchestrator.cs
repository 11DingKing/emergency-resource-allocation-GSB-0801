using System.Data;
using EmergencyDispatch.Domain;
using EmergencyDispatch.Domain.Solving;
using EmergencyDispatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EmergencyDispatch.Infrastructure.Services;

public sealed record SolveOutcome(AllocationPlan Plan, bool IsReplay);

/// <summary>求解冲突（HTTP 409）：过期快照 / 同输入版本不同快照 / 重复初始求解。</summary>
public sealed class SolveConflictException(
    string conflict,
    string detail,
    IReadOnlyList<FieldDiff> differences,
    Guid? existingPlanId = null) : Exception(detail)
{
    public string Conflict { get; } = conflict;
    public IReadOnlyList<FieldDiff> Differences { get; } = differences;
    public Guid? ExistingPlanId { get; } = existingPlanId;
}

/// <summary>
/// 调度编排：在 API 与求解器之间做事务、幂等、快照绑定与审计。
/// - 求解请求必须引用世界快照；快照与当前世界摘要不一致 → 409 stale_snapshot（字段级差异）；
/// - 同一 inputVersion 配不同快照 → 409 input_version_snapshot_conflict（字段级差异），绝不把旧方案伪装成成功；
/// - 完全相同的请求（inputVersion + 同一快照）→ 幂等回放同一方案；
/// - 方案、分配、任务状态、旧版本作废全部在同一事务提交，半套分配永远不会被外界看到。
/// </summary>
public sealed class AllocationOrchestrator(DispatchDbContext db, IAllocationSolver solver, WorldSnapshotService snapshots)
{
    private const int MaxAttempts = 5;

    public async Task<SolveOutcome> SolveAsync(
        string inputVersion, PlanKind kind, string? reason, Guid worldSnapshotId, CancellationToken ct = default)
    {
        var transactional = db.Database.IsRelational();
        var referenced = await snapshots.LoadAsync(worldSnapshotId, ct)
            ?? throw new SolveConflictException("unknown_snapshot",
                $"世界快照 {worldSnapshotId} 不存在，请先 POST /api/world/snapshots 捕获。", Array.Empty<FieldDiff>());
        var referencedEntries = referenced.Entries.Select(WorldSnapshotService.ToInfo).ToList();

        for (var attempt = 1; ; attempt++)
        {
            var existing = await LoadPlanByInputVersionAsync(inputVersion, ct);
            if (existing is not null)
                return ReplayOrConflict(existing, referenced, referencedEntries);

            await using var tx = transactional
                ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
                : null;
            try
            {
                existing = await LoadPlanByInputVersionAsync(inputVersion, ct);
                if (existing is not null)
                {
                    var outcome = ReplayOrConflict(existing, referenced, referencedEntries);
                    if (tx is not null) await tx.CommitAsync(ct);
                    return outcome;
                }

                if (kind == PlanKind.Initial && await db.Plans.AnyAsync(p => p.Status == PlanStatus.Committed, ct))
                    throw new SolveConflictException("initial_already_committed",
                        "已存在生效中的分配方案，初始求解只能执行一次，请使用 replan。", Array.Empty<FieldDiff>());

                // 快照必须等于当前世界状态（RepeatableRead 内一致读取），否则为过期引用
                var currentEntries = await snapshots.BuildCurrentEntriesAsync(ct);
                var currentDigest = WorldSnapshotService.ComputeWorldDigest(currentEntries);
                if (!string.Equals(currentDigest, referenced.WorldDigest, StringComparison.Ordinal))
                {
                    throw new SolveConflictException("stale_snapshot",
                        $"引用的世界快照 v{referenced.SnapshotVersion} 已过期，与当前世界状态不一致，请重新捕获后再求解。",
                        WorldSnapshotService.Diff(referencedEntries, currentEntries));
                }

                // 一次性读取一致性快照（与上方摘要校验同一事务快照）
                var teams = await db.Teams.Include(t => t.Vehicle).ToListAsync(ct);
                var tasks = await db.Tasks.ToListAsync(ct);
                var roads = await db.RoadSegments.ToListAsync(ct);

                var options = new SolveOptions(
                    AllowPreemption: kind == PlanKind.Replan
                        && tasks.Any(t => t.Kind == TaskKind.LifeSafety && t.Danger == DangerLevel.Critical),
                    PreemptionReason: reason);

                var input = new SolveInput(
                    teams.Select(t => new TeamSnapshot(t.Id, t.Code, t.Name,
                        t.Capabilities.ToHashSet(StringComparer.Ordinal),
                        t.Vehicle?.Name ?? "无车辆", t.Vehicle?.HeightMeters ?? 0m)).ToArray(),
                    tasks.Select(t => new TaskSnapshot(t.Id, t.Code, t.Title, t.Kind,
                        t.RequiredCapabilities.ToHashSet(StringComparer.Ordinal),
                        t.DeadlineMinutes, t.DurationMinutes, t.Danger, t.Status, t.CurrentTeamId)).ToArray(),
                    roads.Select(r => new RoadSnapshot(r.Id, r.Code, r.Name, r.MaxVehicleHeightMeters, r.TravelMinutes, r.IsBlocked)).ToArray(),
                    options);

                // 纯内存求解：算法不进控制器，也不碰数据库
                var result = solver.Solve(input);

                var plan = new AllocationPlan
                {
                    Id = Guid.NewGuid(),
                    InputVersion = inputVersion,
                    Kind = kind,
                    Reason = reason,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    Status = result.IsFeasible ? PlanStatus.Committed : PlanStatus.Infeasible,
                    TotalCostMinutes = result.TotalCostMinutes,
                    WorldSnapshotId = referenced.Id
                };

                // 关系库由 identity 列生成版本号；非关系提供程序（测试）显式递增
                if (!transactional)
                    plan.PlanVersion = (await db.Plans.Select(p => (long?)p.PlanVersion).MaxAsync(ct) ?? 0) + 1;

                if (result.IsFeasible)
                {
                    var current = await db.Plans.FirstOrDefaultAsync(p => p.Status == PlanStatus.Committed, ct);
                    if (current is not null)
                    {
                        current.Status = PlanStatus.Superseded;
                        plan.SupersedesPlanId = current.Id;
                    }

                    var tasksById = tasks.ToDictionary(t => t.Id);
                    foreach (var d in result.Assignments)
                    {
                        plan.Assignments.Add(new Assignment
                        {
                            Id = Guid.NewGuid(),
                            PlanId = plan.Id,
                            TaskId = d.TaskId,
                            TeamId = d.TeamId,
                            RoadId = d.RoadId,
                            EtaMinutes = d.EtaMinutes,
                            Cost = d.Cost,
                            IsPreemption = d.IsPreemption,
                            Reasons = d.Reasons.Select(r => new ReasonEntry { Code = r.Code, Message = r.Message }).ToList()
                        });

                        var task = tasksById[d.TaskId];
                        task.CurrentTeamId = d.TeamId;
                        task.Status = task.Status == DispatchTaskStatus.InProgress && !d.IsPreemption
                            ? DispatchTaskStatus.InProgress
                            : DispatchTaskStatus.Assigned;
                    }
                }
                else
                {
                    foreach (var u in result.Unassigned)
                    {
                        plan.Unassigned.Add(new UnassignedTask
                        {
                            Id = Guid.NewGuid(),
                            PlanId = plan.Id,
                            TaskId = u.TaskId,
                            Reasons = u.Reasons.Select(r => new ReasonEntry { Code = r.Code, Message = r.Message }).ToList()
                        });
                    }
                }

                db.Plans.Add(plan);
                await db.SaveChangesAsync(ct);
                if (tx is not null) await tx.CommitAsync(ct);

                return new SolveOutcome(await LoadPlanByInputVersionAsync(inputVersion, ct) ?? plan, IsReplay: false);
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsRetryable(ex))
            {
                // 并发同一输入版本 / 求解中途快照被更新：回滚后重试，重试时走幂等回放、冲突或基于新快照重解
                if (tx is not null) await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>同 inputVersion：快照一致 → 幂等回放；快照不同 → 409 + 字段级差异，绝不伪装成功。</summary>
    private static SolveOutcome ReplayOrConflict(AllocationPlan existing, WorldSnapshot referenced, List<SnapshotEntryInfo> referencedEntries)
    {
        if (existing.WorldSnapshotId == referenced.Id)
            return new SolveOutcome(existing, IsReplay: true);

        var existingEntries = existing.WorldSnapshot?.Entries.Select(WorldSnapshotService.ToInfo).ToList()
            ?? new List<SnapshotEntryInfo>();
        throw new SolveConflictException("input_version_snapshot_conflict",
            $"inputVersion '{existing.InputVersion}' 已绑定世界快照 v{existing.WorldSnapshot?.SnapshotVersion}，" +
            $"与本次引用的 v{referenced.SnapshotVersion} 不一致；如需基于新世界状态求解，请使用新的 inputVersion。",
            WorldSnapshotService.Diff(existingEntries, referencedEntries),
            existing.Id);
    }

    public Task<AllocationPlan?> LoadPlanByInputVersionAsync(string inputVersion, CancellationToken ct) =>
        WithDetails(db.Plans).FirstOrDefaultAsync(p => p.InputVersion == inputVersion, ct);

    public Task<AllocationPlan?> LoadPlanByIdAsync(Guid id, CancellationToken ct) =>
        WithDetails(db.Plans).FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<AllocationPlan?> LoadCurrentAsync(CancellationToken ct) =>
        WithDetails(db.Plans).FirstOrDefaultAsync(p => p.Status == PlanStatus.Committed, ct);

    private IQueryable<AllocationPlan> WithDetails(IQueryable<AllocationPlan> q)
    {
        var query = q
            .Include(p => p.Assignments).ThenInclude(a => a.Task)
            .Include(p => p.Assignments).ThenInclude(a => a.Team).ThenInclude(t => t!.Vehicle)
            .Include(p => p.Assignments).ThenInclude(a => a.Road)
            .Include(p => p.Unassigned).ThenInclude(u => u.Task)
            .Include(p => p.WorldSnapshot).ThenInclude(s => s!.Entries);
        return db.Database.IsRelational() ? query.AsSplitQuery() : query;
    }

    private static bool IsRetryable(Exception ex) => ex switch
    {
        DbUpdateConcurrencyException => true,
        DbUpdateException { InnerException: PostgresException pg } =>
            pg.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure,
        PostgresException pg => pg.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure,
        _ => false
    };
}
