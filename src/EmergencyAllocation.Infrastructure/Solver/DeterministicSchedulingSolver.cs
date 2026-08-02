using System.Text;
using EmergencyAllocation.Core;
using EmergencyAllocation.Core.Solving;
using TaskStatus = EmergencyAllocation.Core.TaskStatus;

namespace EmergencyAllocation.Infrastructure.Solver;

public sealed class DeterministicSchedulingSolver : ISchedulingSolver
{
    private const string SolverVersionString = "deterministic-1.0";
    private const long UnassignedPenalty = 1_000_000;

    public string Version => SolverVersionString;

    public Task<SolverResult> SolveAsync(SchedulingProblem problem, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SolveCore(problem));
    }

    private SolverResult SolveCore(SchedulingProblem problem)
    {
        var ruleExplanations = new List<SolverExplanation>();
        var teams = problem.Teams
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .ToList();
        var tasks = problem.Tasks
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .ToList();

        var inProgress = tasks
            .Where(t => t.Status == TaskStatus.InProgress && t.AssignedTeamId is not null)
            .ToList();

        if (!string.IsNullOrEmpty(problem.TriggeringRoadEventId))
        {
            var affectedRoad = problem.Roads.FirstOrDefault(r =>
                string.Equals(r.ClosedByEventId, problem.TriggeringRoadEventId, StringComparison.Ordinal));
            var affectedTasks = string.Join(",", tasks
                .Where(t => t.DangerLevel >= DangerLevel.LifeSafety || t.Status == TaskStatus.InProgress)
                .Select(t => t.Id)
                .OrderBy(t => t, StringComparer.Ordinal));
            ruleExplanations.Add(new SolverExplanation(
                ExplanationKind.RoadBlocked,
                RuleCodes.RoadClosed,
                $"道路事件 {problem.TriggeringRoadEventId} 已记录" +
                (affectedRoad is not null ? $"，路段 {affectedRoad.Id}（{affectedRoad.FromNode}↔{affectedRoad.ToNode}，限高{affectedRoad.HeightLimitMeters}m）被关闭" : string.Empty) +
                $"；受影响的高风险任务: {affectedTasks}。",
                null,
                null));
        }

        var preemptable = DeterminePreemptableTasks(problem, teams, inProgress, ruleExplanations);
        var combinations = EnumeratePreemptionCombinations(inProgress, preemptable);

        Solution? best = null;
        var diagnosticExplanations = new List<SolverExplanation>();

        foreach (var combination in combinations)
        {
            var candidateExplanations = new List<SolverExplanation>();
            var feasible = TryEvaluateCombination(problem, teams, tasks, inProgress, combination, candidateExplanations,
                out var solution);
            MergeExplanations(diagnosticExplanations, candidateExplanations);
            if (feasible && solution is not null)
            {
                if (best is null || IsBetter(solution, best))
                {
                    if (best is not null && solution.TotalCost == best.TotalCost)
                    {
                        candidateExplanations.Add(new SolverExplanation(
                            ExplanationKind.TieBreak,
                            RuleCodes.TieBreak,
                            $"存在同等成本 {solution.TotalCost} 的候选方案，按确定性tie-break（按任务ID排序后队伍ID字典序最小）选择方案 {solution.Signature} 而非 {best.Signature}。"));
                    }

                    best = solution;
                }
            }
        }

        if (best is null)
        {
            var reason = BuildInfeasibilityReason(tasks, teams, problem);
            var infeasibleExplanations = new List<SolverExplanation>(ruleExplanations);
            MergeExplanations(infeasibleExplanations, diagnosticExplanations);
            infeasibleExplanations.Add(new SolverExplanation(
                ExplanationKind.NoFeasibleSolution,
                RuleCodes.LifeSafetyMustAssign,
                reason));
            return new SolverResult
            {
                IsFeasible = false,
                NoFeasibleReason = reason,
                Assignments = Array.Empty<AssignmentDecision>(),
                Explanations = infeasibleExplanations,
                TotalCost = long.MaxValue,
                SolverVersion = SolverVersionString
            };
        }

        AddPreemptionExplanations(best, inProgress, best.Explanations);

        var allExplanations = ruleExplanations.Concat(best.Explanations).ToList();

        return new SolverResult
        {
            IsFeasible = true,
            Assignments = best.Assignments.OrderBy(a => a.OrderIndex).ToList(),
            Explanations = allExplanations,
            TotalCost = best.TotalCost,
            SolverVersion = SolverVersionString
        };
    }

    private static Dictionary<string, bool> DeterminePreemptableTasks(
        SchedulingProblem problem,
        List<TeamState> teams,
        List<TaskState> inProgress,
        List<SolverExplanation> explanations)
    {
        var result = new Dictionary<string, bool>();

        foreach (var task in inProgress)
        {
            var originalTeam = teams.First(t => t.Id == task.AssignedTeamId);
            if (!problem.Options.DangerLevelRaised || !problem.Options.AllowPreemption)
            {
                result[task.Id] = false;
                explanations.Add(new SolverExplanation(
                    ExplanationKind.Rule,
                    RuleCodes.NonPreemptive,
                    $"任务 {task.Id} 已由队伍 {originalTeam.Id} 执行；生命危险等级未上升或未授权重排，默认不可抢占。",
                    task.Id, originalTeam.Id));
                continue;
            }

            var substitute = teams
                .Where(t => t.Id != originalTeam.Id)
                .Where(t => t.IsAvailable)
                .Where(t => task.RequiredCapabilities.IsSubsetOf(t.Capabilities))
                .FirstOrDefault(t =>
                {
                    var route = RoutePlanner.FindRoute(problem.Roads, t.VehicleHeightMeters, t.CurrentNode,
                        task.LocationNode);
                    return route.IsFeasible && route.TravelTimeMinutes <= task.RequiredArrivalMinutes;
                });

            if (substitute is not null)
            {
                result[task.Id] = true;
                explanations.Add(new SolverExplanation(
                    ExplanationKind.Preemption,
                    RuleCodes.PreemptionAllowed,
                    $"任务 {task.Id} 生命危险等级上升，存在具备全部能力的替代队伍 {substitute.Id}（{string.Join(",", task.RequiredCapabilities.OrderBy(c => c, StringComparer.Ordinal))}），允许重排。",
                    task.Id, substitute.Id));
            }
            else
            {
                result[task.Id] = false;
                explanations.Add(new SolverExplanation(
                    ExplanationKind.Rule,
                    RuleCodes.PreemptionDenied,
                    $"任务 {task.Id} 生命危险等级虽上升，但不存在具备全部能力且能在 {task.RequiredArrivalMinutes} 分钟内到达的替代队伍，不可抢占。",
                    task.Id, originalTeam.Id));
            }
        }

        return result;
    }

    private static List<HashSet<string>> EnumeratePreemptionCombinations(
        List<TaskState> inProgress,
        Dictionary<string, bool> preemptable)
    {
        var eligible = inProgress.Where(t => preemptable[t.Id]).ToList();
        var combinations = new List<HashSet<string>>();
        var count = eligible.Count;

        for (var mask = 0; mask < (1 << count); mask++)
        {
            var set = new HashSet<string>();
            for (var i = 0; i < count; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    set.Add(eligible[i].Id);
                }
            }

            combinations.Add(set);
        }

        return combinations;
    }

    private static bool TryEvaluateCombination(
        SchedulingProblem problem,
        List<TeamState> teams,
        List<TaskState> tasks,
        List<TaskState> inProgress,
        HashSet<string> preemptedTaskIds,
        List<SolverExplanation> explanations,
        out Solution solution)
    {
        solution = null!;
        var decisions = new List<AssignmentDecision>();
        var usedTeams = new HashSet<string>();
        long cost = 0;

        foreach (var task in inProgress)
        {
            var teamId = task.AssignedTeamId!;
            var team = teams.First(t => t.Id == teamId);

            if (preemptedTaskIds.Contains(task.Id))
            {
                continue;
            }

            var route = RoutePlanner.FindRoute(problem.Roads, team.VehicleHeightMeters, team.CurrentNode,
                task.LocationNode);
            if (!route.IsFeasible || route.TravelTimeMinutes > task.RequiredArrivalMinutes)
            {
                return false;
            }

            usedTeams.Add(teamId);
            cost += DangerWeight(task.DangerLevel) * route.TravelTimeMinutes;
            decisions.Add(new AssignmentDecision(
                task.Id, teamId, team.VehicleId, route.Nodes,
                route.TravelTimeMinutes,
                route.TravelTimeMinutes + task.DurationMinutes,
                AssignmentKind.Kept,
                null,
                0));
        }

        var openTasks = tasks
            .Where(t => t.Status != TaskStatus.Completed && !inProgress.Any(ip => ip.Id == t.Id && !preemptedTaskIds.Contains(ip.Id)))
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .ToList();

        var availableTeams = teams
            .Where(t => t.IsAvailable && !usedTeams.Contains(t.Id))
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .ToList();

        var feasibility = BuildFeasibility(problem, openTasks, availableTeams, explanations);

        var bestOpenSolution = EnumerateOpenAssignments(
            openTasks, availableTeams, feasibility, 0,
            new Dictionary<string, string>(), new HashSet<string>(), 0L);

        if (bestOpenSolution is null)
        {
            return false;
        }

        cost += bestOpenSolution.Cost;

        foreach (var task in openTasks)
        {
            if (bestOpenSolution.Assignment.TryGetValue(task.Id, out var teamId))
            {
                var team = availableTeams.First(t => t.Id == teamId);
                var f = feasibility[task.Id][teamId];
                var kind = task.Status == TaskStatus.InProgress
                    ? AssignmentKind.ReassignedTo
                    : AssignmentKind.NewAssignment;
                string? preemptionReason = null;
                if (kind == AssignmentKind.ReassignedTo)
                {
                    var original = task.AssignedTeamId!;
                    preemptionReason =
                        $"生命危险等级上升且替代队伍 {teamId} 具备任务所需全部能力；原执行队伍 {original} 因路线/调度需要被抢占。";
                }

                decisions.Add(new AssignmentDecision(
                    task.Id, teamId, team.VehicleId, f.Route.Nodes,
                    f.Route.TravelTimeMinutes,
                    f.Route.TravelTimeMinutes + task.DurationMinutes,
                    kind, preemptionReason, 0));
            }
            else
            {
                decisions.Add(new AssignmentDecision(
                    task.Id, null, null, Array.Empty<string>(), 0, 0,
                    AssignmentKind.Unassigned, null, 0));
            }
        }

        for (var i = 0; i < decisions.Count; i++)
        {
            decisions[i] = decisions[i] with { OrderIndex = i };
        }

        var signature = BuildSignature(decisions);
        solution = new Solution(decisions, cost, signature, preemptedTaskIds, explanations);
        return true;
    }

    private static OpenSolution? EnumerateOpenAssignments(
        List<TaskState> openTasks,
        List<TeamState> availableTeams,
        Dictionary<string, Dictionary<string, Feasibility>> feasibility,
        int taskIndex,
        Dictionary<string, string> current,
        HashSet<string> usedTeams,
        long runningCost)
    {
        if (taskIndex == openTasks.Count)
        {
            return new OpenSolution(new Dictionary<string, string>(current), runningCost);
        }

        var task = openTasks[taskIndex];
        var mustAssign = task.DangerLevel >= DangerLevel.LifeSafety || task.Status == TaskStatus.InProgress;
        var taskWeight = DangerWeight(task.DangerLevel);

        OpenSolution? best = null;

        foreach (var team in availableTeams)
        {
            if (usedTeams.Contains(team.Id))
            {
                continue;
            }

            var f = feasibility[task.Id][team.Id];
            if (!f.IsFeasible)
            {
                continue;
            }

            current[task.Id] = team.Id;
            usedTeams.Add(team.Id);
            var addedCost = taskWeight * f.Route.TravelTimeMinutes;
            var candidate = EnumerateOpenAssignments(openTasks, availableTeams, feasibility,
                taskIndex + 1, current, usedTeams, runningCost + addedCost);
            if (candidate is not null && (best is null || IsBetterOpen(candidate, best, openTasks)))
            {
                best = candidate;
            }

            usedTeams.Remove(team.Id);
            current.Remove(task.Id);
        }

        if (!mustAssign)
        {
            var candidate = EnumerateOpenAssignments(openTasks, availableTeams, feasibility,
                taskIndex + 1, current, usedTeams, runningCost + UnassignedPenalty);
            if (candidate is not null && (best is null || IsBetterOpen(candidate, best, openTasks)))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static bool IsBetterOpen(OpenSolution candidate, OpenSolution best, List<TaskState> openTasks)
    {
        if (candidate.Cost != best.Cost)
        {
            return candidate.Cost < best.Cost;
        }

        var candidateSignature = BuildOpenSignature(candidate.Assignment, openTasks);
        var bestSignature = BuildOpenSignature(best.Assignment, openTasks);
        return string.CompareOrdinal(candidateSignature, bestSignature) < 0;
    }

    private static string BuildOpenSignature(Dictionary<string, string> assignment, List<TaskState> openTasks)
    {
        var sb = new StringBuilder();
        foreach (var task in openTasks.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            if (sb.Length > 0)
            {
                sb.Append('|');
            }

            sb.Append(task.Id).Append(':');
            sb.Append(assignment.TryGetValue(task.Id, out var team) ? team : "~");
        }

        return sb.ToString();
    }

    private static Dictionary<string, Dictionary<string, Feasibility>> BuildFeasibility(
        SchedulingProblem problem,
        List<TaskState> openTasks,
        List<TeamState> availableTeams,
        List<SolverExplanation> explanations)
    {
        var result = new Dictionary<string, Dictionary<string, Feasibility>>();

        foreach (var task in openTasks)
        {
            var perTeam = new Dictionary<string, Feasibility>();
            result[task.Id] = perTeam;

            foreach (var team in availableTeams)
            {
                var reasons = new List<string>();
                var hasCaps = task.RequiredCapabilities.IsSubsetOf(team.Capabilities);
                if (!hasCaps)
                {
                    var missing = task.RequiredCapabilities
                        .Where(c => !team.Capabilities.Contains(c))
                        .OrderBy(c => c, StringComparer.Ordinal);
                    reasons.Add($"缺少能力 {string.Join(",", missing)}");
                }

                var route = RoutePlanner.FindRoute(problem.Roads, team.VehicleHeightMeters, team.CurrentNode,
                    task.LocationNode);
                if (!route.IsFeasible)
                {
                    reasons.Add(route.BlockReason ?? "无可达路线");
                }
                else if (route.TravelTimeMinutes > task.RequiredArrivalMinutes)
                {
                    reasons.Add(
                        $"到达时间 {route.TravelTimeMinutes} 分钟超过时限 {task.RequiredArrivalMinutes} 分钟");
                }

                var feasible = hasCaps && route.IsFeasible && route.TravelTimeMinutes <= task.RequiredArrivalMinutes;
                perTeam[team.Id] = new Feasibility(route, feasible, reasons);

                if (!feasible)
                {
                    var ruleCode = !hasCaps
                        ? RuleCodes.CapabilityMatch
                        : !route.IsFeasible
                            ? RuleCodes.HeightLimit
                            : RuleCodes.ArrivalDeadline;
                    var kind = !route.IsFeasible ? ExplanationKind.RoadBlocked : ExplanationKind.Warning;
                    explanations.Add(new SolverExplanation(
                        kind,
                        ruleCode,
                        $"队伍 {team.Id}（车高 {team.VehicleHeightMeters}m）不能承担任务 {task.Id}：{string.Join("；", reasons)}",
                        task.Id, team.Id));
                }
            }
        }

        return result;
    }

    private static bool IsBetter(Solution candidate, Solution best)
    {
        if (candidate.TotalCost != best.TotalCost)
        {
            return candidate.TotalCost < best.TotalCost;
        }

        return string.CompareOrdinal(candidate.Signature, best.Signature) < 0;
    }

    private static void MergeExplanations(List<SolverExplanation> target, List<SolverExplanation> source)
    {
        foreach (var explanation in source)
        {
            var exists = target.Any(t =>
                t.Kind == explanation.Kind &&
                t.RuleCode == explanation.RuleCode &&
                t.Message == explanation.Message &&
                t.RelatedTaskId == explanation.RelatedTaskId &&
                t.RelatedTeamId == explanation.RelatedTeamId);
            if (!exists)
            {
                target.Add(explanation);
            }
        }
    }

    private static string BuildSignature(List<AssignmentDecision> decisions)
    {
        var sb = new StringBuilder();
        foreach (var decision in decisions.OrderBy(d => d.TaskId, StringComparer.Ordinal))
        {
            if (sb.Length > 0)
            {
                sb.Append('|');
            }

            sb.Append(decision.TaskId).Append(':').Append(decision.TeamId ?? "~");
        }

        return sb.ToString();
    }

    private static long DangerWeight(DangerLevel level) => level switch
    {
        DangerLevel.Critical => 10_000,
        DangerLevel.LifeSafety => 1000,
        DangerLevel.Elevated => 100,
        DangerLevel.Routine => 10,
        _ => 10
    };

    private static string BuildInfeasibilityReason(
        List<TaskState> tasks,
        List<TeamState> teams,
        SchedulingProblem problem)
    {
        var lifeTasks = tasks.Where(t => t.DangerLevel >= DangerLevel.LifeSafety).ToList();
        var reasons = new List<string>();

        foreach (var task in lifeTasks)
        {
            var candidateTeams = teams
                .Where(t => task.RequiredCapabilities.IsSubsetOf(t.Capabilities))
                .ToList();

            if (candidateTeams.Count == 0)
            {
                reasons.Add($"生命安全任务 {task.Id} 无队伍具备所需能力 {string.Join(",", task.RequiredCapabilities)}");
                continue;
            }

            foreach (var team in candidateTeams)
            {
                var route = RoutePlanner.FindRoute(problem.Roads, team.VehicleHeightMeters, team.CurrentNode,
                    task.LocationNode);
                if (!route.IsFeasible)
                {
                    reasons.Add($"队伍 {team.Id} 无法到达 {task.Id}：{route.BlockReason}");
                }
                else if (route.TravelTimeMinutes > task.RequiredArrivalMinutes)
                {
                    reasons.Add(
                        $"队伍 {team.Id} 到达 {task.Id} 需 {route.TravelTimeMinutes} 分钟，超过 {task.RequiredArrivalMinutes} 分钟时限");
                }
            }
        }

        return reasons.Count == 0
            ? "不存在满足全部约束的可行分配方案。"
            : string.Join("；", reasons);
    }

    private static void AddPreemptionExplanations(
        Solution best,
        List<TaskState> inProgress,
        List<SolverExplanation> explanations)
    {
        foreach (var taskId in best.PreemptedTaskIds)
        {
            var task = inProgress.First(t => t.Id == taskId);
            var originalTeam = task.AssignedTeamId!;
            var newTeam = best.Assignments.First(a => a.TaskId == taskId).TeamId;
            explanations.Add(new SolverExplanation(
                ExplanationKind.Preemption,
                RuleCodes.PreemptionAllowed,
                $"任务 {taskId} 由原执行队伍 {originalTeam} 改派至 {newTeam}；原因：生命危险等级上升，{newTeam} 具备全部所需能力，且原方案在当前道路快照下不再最优/可行。",
                taskId, newTeam));
        }
    }

    private readonly record struct Feasibility(RouteInfo Route, bool IsFeasible, List<string> Reasons);

    private sealed class OpenSolution
    {
        public OpenSolution(Dictionary<string, string> assignment, long cost)
        {
            Assignment = assignment;
            Cost = cost;
        }

        public Dictionary<string, string> Assignment { get; }
        public long Cost { get; }
    }

    private sealed class Solution
    {
        public Solution(
            List<AssignmentDecision> assignments,
            long totalCost,
            string signature,
            HashSet<string> preemptedTaskIds,
            List<SolverExplanation> explanations)
        {
            Assignments = assignments;
            TotalCost = totalCost;
            Signature = signature;
            PreemptedTaskIds = preemptedTaskIds;
            Explanations = explanations;
        }

        public List<AssignmentDecision> Assignments { get; }
        public long TotalCost { get; }
        public string Signature { get; }
        public HashSet<string> PreemptedTaskIds { get; }
        public List<SolverExplanation> Explanations { get; }
    }
}
