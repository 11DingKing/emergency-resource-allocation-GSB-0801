using EmergencyDispatch.Domain;
using EmergencyDispatch.Domain.Solving;

namespace EmergencyDispatch.Solver;

/// <summary>
/// 确定性求解器：纯函数、无 IO、无随机、无时钟依赖。
/// 目标函数：最小化全部开放任务的 ETA 总和（分钟）。
/// 决胜规则（tie-break）：总成本相同时，按任务编码顺序展开的队伍编码向量取字典序最小者。
/// 抢占规则：默认执行中任务不可抢占；仅当编排层判定"生命危险等级上升"（AllowPreemption）
/// 且存在具备全部所需能力的替代队伍时，才把执行中任务放回开放池，并在决定上记录原因。
/// </summary>
public sealed class DeterministicAllocationSolver : IAllocationSolver
{
    public SolveResult Solve(SolveInput input)
    {
        var teams = input.Teams.OrderBy(t => t.Code, StringComparer.Ordinal).ToArray();
        var roads = input.Roads.OrderBy(r => r.Code, StringComparer.Ordinal).ToArray();
        var allTasks = input.Tasks.OrderBy(t => t.Code, StringComparer.Ordinal).ToArray();

        var locked = allTasks.Where(t => t.Status == DispatchTaskStatus.InProgress).ToArray();
        var open = allTasks.Where(t => t.Status != DispatchTaskStatus.InProgress).ToArray();

        var attempt = SolveCore(teams, roads, open, locked, preemptionMode: false, input.Options);
        if (attempt.IsFeasible)
            return attempt;

        if (input.Options.AllowPreemption && locked.Length > 0)
        {
            // 生命危险等级上升：把执行中任务放回开放池重排。
            var reopened = open.Concat(locked).OrderBy(t => t.Code, StringComparer.Ordinal).ToArray();
            var retry = SolveCore(teams, roads, reopened, Array.Empty<TaskSnapshot>(), preemptionMode: true, input.Options);
            if (!retry.IsFeasible)
                return retry;

            var incumbents = locked.Where(t => t.IncumbentTeamId.HasValue)
                                   .ToDictionary(t => t.Id, t => t.IncumbentTeamId!.Value);
            var teamCode = teams.ToDictionary(t => t.Id, t => t.Code);
            var annotated = retry.Assignments.Select(a =>
            {
                if (!incumbents.TryGetValue(a.TaskId, out var incumbent) || incumbent == a.TeamId)
                    return a;
                var reasons = a.Reasons.Append(new ReasonEntry
                {
                    Code = ReasonCodes.PreemptionDangerEscalation,
                    Message = $"生命危险等级上升，按规则允许重排：原执行队伍 {teamCode.GetValueOrDefault(incumbent)} 被换下，" +
                              $"接替队伍 {teamCode.GetValueOrDefault(a.TeamId)} 具备该任务全部所需能力。" +
                              $"触发原因：{input.Options.PreemptionReason ?? "未填写"}"
                }).ToArray();
                return a with { IsPreemption = true, Reasons = reasons };
            }).ToArray();
            return retry with { Assignments = annotated };
        }

        return attempt;
    }

    private sealed record TeamEval(bool Ok, RoadSnapshot? Road, int Eta, IReadOnlyList<ReasonEntry> Reasons);

    private static SolveResult SolveCore(
        TeamSnapshot[] teams,
        RoadSnapshot[] roads,
        TaskSnapshot[] open,
        TaskSnapshot[] locked,
        bool preemptionMode,
        SolveOptions options)
    {
        var decisions = new List<AssignmentDecision>();
        var busyByTeam = new Dictionary<Guid, string>(); // teamId -> taskCode
        var reopened = new List<TaskSnapshot>();

        foreach (var lt in locked)
        {
            var incumbent = teams.FirstOrDefault(t => t.Id == lt.IncumbentTeamId);
            if (incumbent is null)
            {
                reopened.Add(lt); // 原队伍已不存在，退回开放池
                continue;
            }
            busyByTeam[incumbent.Id] = lt.Code;
            decisions.Add(new AssignmentDecision(lt.Id, incumbent.Id, null, 0, 0, false,
                new ReasonEntry[]
                {
                    new() { Code = ReasonCodes.LockedInProgress,
                            Message = $"任务 {lt.Code} 正在执行中，按默认规则不可抢占，继续由 {incumbent.Code} 执行" }
                }));
        }

        if (reopened.Count > 0)
            open = open.Concat(reopened).OrderBy(t => t.Code, StringComparer.Ordinal).ToArray();

        var available = teams.Where(t => !busyByTeam.ContainsKey(t.Id)).ToArray();

        // 1) 评估每个（任务, 队伍）组合：能力 / 道路（中断、限高）/ 到达时限
        var evals = new Dictionary<Guid, Dictionary<Guid, TeamEval>>();
        foreach (var task in open)
        {
            var row = new Dictionary<Guid, TeamEval>();
            foreach (var team in available)
                row[team.Id] = Evaluate(team, task, roads);
            evals[task.Id] = row;
        }

        // 2) 回溯搜索完整分配（每队最多一个任务），收集全部最优向量用于确定性 tie-break
        var chosen = new (Guid TeamId, Guid? RoadId, int Eta)?[open.Length];
        var optimal = new List<(Guid TeamId, Guid? RoadId, int Eta)[]>();
        var bestTotal = int.MaxValue;
        var used = new bool[available.Length];

        void Dfs(int i, int cost)
        {
            if (cost > bestTotal) return;
            if (i == open.Length)
            {
                var vector = chosen.Select(c => c!.Value).ToArray();
                if (cost < bestTotal)
                {
                    bestTotal = cost;
                    optimal.Clear();
                }
                optimal.Add(vector);
                return;
            }
            var task = open[i];
            for (var k = 0; k < available.Length; k++)
            {
                if (used[k]) continue;
                var ev = evals[task.Id][available[k].Id];
                if (!ev.Ok) continue;
                used[k] = true;
                chosen[i] = (available[k].Id, ev.Road!.Id, ev.Eta);
                Dfs(i + 1, cost + ev.Eta);
                used[k] = false;
                chosen[i] = null;
            }
        }

        if (open.Length > 0)
            Dfs(0, 0);

        // 3) 无可行解：不输出任何分配（绝不产生半套分配），逐任务给出不可分配原因
        if (optimal.Count == 0)
        {
            var unassigned = new List<UnassignedDecision>();
            foreach (var task in open)
            {
                var row = evals[task.Id];
                var reasons = new List<ReasonEntry>();
                var anyCandidate = row.Values.Any(e => e.Ok);
                if (anyCandidate)
                {
                    reasons.Add(new ReasonEntry
                    {
                        Code = ReasonCodes.ResourceConflict,
                        Message = $"任务 {task.Code} 虽有候选队伍，但与其余任务争夺同一队伍，无法形成完整分配"
                    });
                }
                else
                {
                    foreach (var team in available)
                        reasons.AddRange(row[team.Id].Reasons);
                    foreach (var (teamId, taskCode) in busyByTeam.OrderBy(kv => kv.Value, StringComparer.Ordinal))
                    {
                        var code = teams.First(t => t.Id == teamId).Code;
                        reasons.Add(new ReasonEntry { Code = ReasonCodes.TeamBusy, Message = $"队伍 {code} 正在执行 {taskCode}，不可调度" });
                    }
                    if (reasons.Count == 0)
                        reasons.Add(new ReasonEntry { Code = ReasonCodes.ResourceConflict, Message = "没有可用队伍" });
                }
                unassigned.Add(new UnassignedDecision(task.Id, reasons));
            }
            return new SolveResult(false, Array.Empty<AssignmentDecision>(), unassigned, 0);
        }

        // 4) 确定性 tie-break：最优向量集合中按队伍编码序列取字典序最小
        var teamCodeOf = teams.ToDictionary(t => t.Id, t => t.Code);
        var selected = optimal
            .OrderBy(v => string.Join("\u001f", v.Select(x => teamCodeOf[x.TeamId])), StringComparer.Ordinal)
            .First();
        var totalCost = selected.Sum(x => x.Eta);

        for (var i = 0; i < open.Length; i++)
        {
            var task = open[i];
            var (teamId, roadId, eta) = selected[i];
            var team = available.First(t => t.Id == teamId);
            var road = roads.First(r => r.Id == roadId);
            var reasons = new List<ReasonEntry>
            {
                new() { Code = ReasonCodes.MinCost, Message = $"全局总成本最低（合计 {totalCost} 分钟）" },
                new() { Code = ReasonCodes.RouteSelected, Message = $"经 {road.Code}（{road.Name}），预计 {eta} 分钟到达" }
            };

            // 并列决胜说明
            var rivals = optimal.Where(v => v[i].TeamId != teamId)
                                .Select(v => teamCodeOf[v[i].TeamId])
                                .Distinct(StringComparer.Ordinal)
                                .OrderBy(c => c, StringComparer.Ordinal)
                                .ToArray();
            if (rivals.Length > 0)
                reasons.Add(new ReasonEntry
                {
                    Code = ReasonCodes.TieBreakTeamCode,
                    Message = $"与队伍 {string.Join("、", rivals)} 总成本相同（{totalCost} 分钟），按队伍编码字典序决胜，选中 {team.Code}"
                });

            // 其余队伍的落选/排除原因（供解释与差异输出）
            foreach (var other in available)
            {
                if (other.Id == teamId) continue;
                var ev = evals[task.Id][other.Id];
                if (ev.Ok)
                {
                    if (!rivals.Contains(other.Code, StringComparer.Ordinal))
                        reasons.Add(new ReasonEntry
                        {
                            Code = ReasonCodes.CostHigher,
                            Message = $"队伍 {other.Code} 可达（ETA {ev.Eta} 分钟），但未被全局最低成本方案选中"
                        });
                }
                else
                {
                    reasons.AddRange(ev.Reasons);
                }
            }
            foreach (var (busyTeamId, taskCode) in busyByTeam.OrderBy(kv => kv.Value, StringComparer.Ordinal))
            {
                var code = teams.First(t => t.Id == busyTeamId).Code;
                reasons.Add(new ReasonEntry { Code = ReasonCodes.TeamBusy, Message = $"队伍 {code} 正在执行 {taskCode}，不可调度" });
            }

            decisions.Add(new AssignmentDecision(task.Id, teamId, roadId, eta, eta, false, reasons));
        }

        return new SolveResult(true, decisions, Array.Empty<UnassignedDecision>(), decisions.Sum(d => d.Cost));
    }

    private static TeamEval Evaluate(TeamSnapshot team, TaskSnapshot task, RoadSnapshot[] roads)
    {
        var reasons = new List<ReasonEntry>();

        var missing = task.RequiredCapabilities
            .Where(c => !team.Capabilities.Contains(c))
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
            reasons.Add(new ReasonEntry
            {
                Code = ReasonCodes.CapabilityMissing,
                Message = $"队伍 {team.Code} 缺少能力：{string.Join("、", missing)}"
            });

        RoadSnapshot? best = null;
        var routeProblems = new List<ReasonEntry>();
        foreach (var r in roads)
        {
            if (r.IsBlocked)
            {
                routeProblems.Add(new ReasonEntry { Code = ReasonCodes.RoadBlocked, Message = $"道路 {r.Code}（{r.Name}）已中断，队伍 {team.Code} 不可通行" });
                continue;
            }
            if (team.VehicleHeightMeters > r.MaxVehicleHeightMeters)
            {
                routeProblems.Add(new ReasonEntry
                {
                    Code = ReasonCodes.HeightExceeded,
                    Message = $"队伍 {team.Code} 车辆高 {team.VehicleHeightMeters}m，超过 {r.Code} 限高 {r.MaxVehicleHeightMeters}m"
                });
                continue;
            }
            if (best is null || r.TravelMinutes < best.TravelMinutes)
                best = r;
        }

        if (best is null)
        {
            reasons.AddRange(routeProblems);
            reasons.Add(new ReasonEntry { Code = ReasonCodes.NoRouteForTeam, Message = $"队伍 {team.Code} 无可用道路" });
        }
        else if (task.DeadlineMinutes is int deadline && best.TravelMinutes > deadline)
        {
            reasons.Add(new ReasonEntry
            {
                Code = ReasonCodes.DeadlineExceeded,
                Message = $"队伍 {team.Code} 经 {best.Code} 需 {best.TravelMinutes} 分钟，超过到达时限 {deadline} 分钟"
            });
        }

        return new TeamEval(reasons.Count == 0, best, best?.TravelMinutes ?? 0, reasons);
    }
}
