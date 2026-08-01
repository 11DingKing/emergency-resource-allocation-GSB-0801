using System.Data;
using EmergencyDispatch.Domain;
using EmergencyDispatch.Domain.Solving;
using EmergencyDispatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EmergencyDispatch.Infrastructure.Services;

public sealed record SolveOutcome(AllocationPlan Plan, bool IsReplay);

/// <summary>
/// 调度编排：在 API 与求解器之间做事务、幂等与审计。
/// - 道路/任务快照在 RepeatableRead 事务内一次性读取，求解中途的外部变更不会混入本次求解；
/// - InputVersion 唯一约束兜底并发重复提交，唯一冲突/序列化失败/乐观并发失败 → 回滚重试 → 幂等回放；
/// - 方案、分配、任务状态、旧版本作废全部在同一事务提交，半套分配永远不会被外界看到。
/// </summary>
public sealed class AllocationOrchestrator(DispatchDbContext db, IAllocationSolver solver)
{
    private const int MaxAttempts = 5;

    public async Task<SolveOutcome> SolveAsync(string inputVersion, PlanKind kind, string? reason, CancellationToken ct = default)
    {
        var transactional = db.Database.IsRelational();

        for (var attempt = 1; ; attempt++)
        {
            var existing = await LoadPlanByInputVersionAsync(inputVersion, ct);
            if (existing is not null)
                return new SolveOutcome(existing, IsReplay: true);

            // 关系库用 RepeatableRead 保证快照一致与原子提交；InMemory（测试）无事务概念，直接执行
            await using var tx = transactional
                ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
                : null;
            try
            {
                existing = await LoadPlanByInputVersionAsync(inputVersion, ct);
                if (existing is not null)
                {
                    if (tx is not null) await tx.CommitAsync(ct);
                    return new SolveOutcome(existing, IsReplay: true);
                }

                if (kind == PlanKind.Initial && await db.Plans.AnyAsync(p => p.Status == PlanStatus.Committed, ct))
                    throw new InvalidOperationException("已存在生效中的分配方案，初始求解只能执行一次，请使用 replan。");

                // 一次性读取一致性快照（RepeatableRead 保证多语句同一快照）
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
                    TotalCostMinutes = result.TotalCostMinutes
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
                // 并发同一输入版本 / 求解中途快照被更新：回滚后重试，重试时走幂等回放或基于新快照重解
                if (tx is not null) await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
            }
        }
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
            .Include(p => p.Unassigned).ThenInclude(u => u.Task);
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
