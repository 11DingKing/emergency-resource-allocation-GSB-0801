using EmergencyDispatch.Domain;

namespace EmergencyDispatch.Infrastructure.Services;

public sealed record AssignmentRefDto(string TeamCode, string TeamName, string? RoadCode, int EtaMinutes);

public sealed record PlanChangeDto(
    string TaskCode,
    string TaskTitle,
    string ChangeType, // assigned | reassigned | unchanged | dropped
    AssignmentRefDto? From,
    AssignmentRefDto? To,
    string Rule);

public sealed record PlanDiffDto(long? FromPlanVersion, long ToPlanVersion, IReadOnlyList<PlanChangeDto> Changes);

/// <summary>
/// 版本间差异。规则文字直接取自求解时落库的同一份 Reasons，
/// 保证 tie-break、不可分配原因、分配版本与审计解释四者一致。
/// </summary>
public static class PlanDiffBuilder
{
    public static PlanDiffDto Build(AllocationPlan? previous, AllocationPlan current)
    {
        var changes = new List<PlanChangeDto>();
        var prevByTask = previous?.Assignments.ToDictionary(a => a.TaskId) ?? new Dictionary<Guid, Assignment>();

        foreach (var a in current.Assignments.OrderBy(a => a.Task!.Code, StringComparer.Ordinal))
        {
            var to = new AssignmentRefDto(a.Team!.Code, a.Team!.Name, a.Road?.Code, a.EtaMinutes);
            var rule = string.Join("；", a.Reasons.Select(r => r.Message));

            if (!prevByTask.TryGetValue(a.TaskId, out var old))
            {
                changes.Add(new PlanChangeDto(a.Task!.Code, a.Task!.Title, "assigned", null, to, rule));
            }
            else if (old.TeamId != a.TeamId)
            {
                var from = new AssignmentRefDto(old.Team!.Code, old.Team!.Name, old.Road?.Code, old.EtaMinutes);
                changes.Add(new PlanChangeDto(a.Task!.Code, a.Task!.Title, "reassigned", from, to, rule));
            }
            else
            {
                var from = new AssignmentRefDto(old.Team!.Code, old.Team!.Name, old.Road?.Code, old.EtaMinutes);
                changes.Add(new PlanChangeDto(a.Task!.Code, a.Task!.Title, "unchanged", from, to, rule));
            }
        }

        foreach (var u in current.Unassigned.OrderBy(u => u.Task!.Code, StringComparer.Ordinal))
        {
            if (!prevByTask.TryGetValue(u.TaskId, out var old))
                continue;
            var from = new AssignmentRefDto(old.Team!.Code, old.Team!.Name, old.Road?.Code, old.EtaMinutes);
            var rule = string.Join("；", u.Reasons.Select(r => r.Message));
            changes.Add(new PlanChangeDto(u.Task!.Code, u.Task!.Title, "dropped", from, null, rule));
        }

        return new PlanDiffDto(previous?.PlanVersion, current.PlanVersion, changes);
    }
}
