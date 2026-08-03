using System.Globalization;

namespace EmergencyAllocation.Domain.Solver;

public sealed class GreedyAllocationSolver : IAllocationSolver
{
    public string Name => "greedy-deterministic-v1";

    public SolverResult Solve(SolverRequest request)
    {
        var audit = new List<SolverAuditEntry>();
        int auditOrder = 0;
        void Audit(string kind, string msg, string? task = null, string? team = null, string? vehicle = null, string? road = null)
            => audit.Add(new SolverAuditEntry(++auditOrder, kind, task, team, vehicle, road, msg));

        var teams = request.Teams.ToList();
        var tasks = request.Tasks.ToList();

        Audit("solve-start",
            $"Operation={request.Operation}; teams={teams.Count}; tasks={tasks.Count}; roadSnapshot={request.RoadSnapshotVersion}; allowReassign={request.AllowReassign}.");

        var startedTasks = tasks.Where(t => t.HasStarted).ToList();
        var pendingTasks = tasks.Where(t => !t.HasStarted)
            .OrderByDescending(t => (int)t.Severity)
            .ThenBy(t => t.DeadlineMinutes ?? int.MaxValue)
            .ThenBy(t => t.Code, StringComparer.Ordinal)
            .ToList();

        var busyTeams = new HashSet<Guid>();
        var busyVehicles = new HashSet<Guid>();
        var assignments = new List<SolverAssignment>();

        foreach (var st in startedTasks)
        {
            var assignedTeam = st.AssignedTeamId is Guid tid ? teams.FirstOrDefault(x => x.Id == tid) : null;
            var assignedVeh = st.AssignedVehicleId is Guid vid ? teams.SelectMany(x => x.Vehicles).FirstOrDefault(x => x.Id == vid) : null;

            bool blocked = false;
            string? blockReason = null;
            SolverRoute? currentRoute = null;

            if (assignedTeam is not null && assignedVeh is not null)
            {
                var graph = new RoadGraph(request.Roads, assignedVeh.HeightMeters);
                currentRoute = graph.ShortestPath(assignedTeam.BaseNodeId, st.LocationNodeId);
                if (currentRoute is null)
                {
                    blocked = true;
                    blockReason = $"Team {assignedTeam.Code} with vehicle {assignedVeh.Code} can no longer reach {st.LocationNodeId} after road changes.";
                }
            }

            bool teamHasAll = assignedTeam is not null && st.RequiredCapabilities.All(assignedTeam.Capabilities.Contains);
            bool canReassign = request.AllowReassign
                               && st.Severity == TaskSeverity.LifeSafety
                               && (!teamHasAll || blocked);

            if (blocked && !canReassign)
            {
                Audit("task-blocked", blockReason!, st.Code, assignedTeam?.Code, assignedVeh?.Code);
                assignments.Add(new SolverAssignment(
                    st.Id, st.Code, assignedTeam?.Id, assignedTeam?.Code,
                    assignedVeh?.Id, assignedVeh?.Code,
                    AllocationDecision.Kept,
                    currentRoute?.TotalTravelMinutes ?? 0,
                    (currentRoute?.TotalTravelMinutes ?? 0) + st.DurationMinutes,
                    false, currentRoute, blockReason, false, null, null));
                if (assignedTeam is not null) busyTeams.Add(assignedTeam.Id);
                if (assignedVeh is not null) busyVehicles.Add(assignedVeh.Id);
                continue;
            }

            if (canReassign && assignedTeam is not null)
            {
                var candidate = FindBestCandidate(st, teams, request.Roads, busyTeams, busyVehicles,
                    requireDifferent: assignedTeam.Id, audit, ref auditOrder);
                if (candidate is not null)
                {
                    busyTeams.Add(candidate.Team.Id);
                    busyVehicles.Add(candidate.Vehicle.Id);
                    Audit("task-reassigned",
                        $"Started task {st.Code} ({st.Severity}, severityVersion={st.SeverityVersion}) is reassigned from {assignedTeam.Code} to {candidate.Team.Code} with {candidate.Vehicle.Code} because {(blocked ? "route is blocked" : !teamHasAll ? "current team lacks required capabilities" : "severity escalation allows a fully-capable replacement")}; reason: {request.Reason}.",
                        st.Code, candidate.Team.Code, candidate.Vehicle.Code);
                    assignments.Add(new SolverAssignment(
                        st.Id, st.Code, candidate.Team.Id, candidate.Team.Code,
                        candidate.Vehicle.Id, candidate.Vehicle.Code,
                        AllocationDecision.Reassigned,
                        candidate.Route!.TotalTravelMinutes,
                        candidate.Route.TotalTravelMinutes + st.DurationMinutes,
                        !st.DeadlineMinutes.HasValue || candidate.Route.TotalTravelMinutes <= st.DeadlineMinutes.Value,
                        candidate.Route,
                        $"Preempted due to severity escalation. {request.Reason}",
                        true, st.AssignedTeamId, st.AssignedVehicleId));
                    continue;
                }

                Audit("reassign-failed",
                    $"Started task {st.Code} requires reassignment but no fully-capable replacement team is available; keeping {assignedTeam.Code}.",
                    st.Code, assignedTeam.Code, assignedVeh?.Code);
            }

            if (!blocked)
            {
                Audit("task-kept",
                    $"Started task {st.Code} remains with {assignedTeam?.Code ?? "unassigned"}; cannot be preempted per policy (severity={st.Severity}, allowReassign={request.AllowReassign}).",
                    st.Code, assignedTeam?.Code, assignedVeh?.Code);
            }

            assignments.Add(new SolverAssignment(
                st.Id, st.Code, assignedTeam?.Id, assignedTeam?.Code,
                assignedVeh?.Id, assignedVeh?.Code,
                AllocationDecision.Kept,
                currentRoute?.TotalTravelMinutes ?? 0,
                (currentRoute?.TotalTravelMinutes ?? 0) + st.DurationMinutes,
                !st.DeadlineMinutes.HasValue || (currentRoute?.TotalTravelMinutes ?? 0) <= st.DeadlineMinutes.Value,
                currentRoute,
                blocked ? blockReason : "Task already in progress; cannot be preempted per policy.",
                false, null, null));
            if (assignedTeam is not null) busyTeams.Add(assignedTeam.Id);
            if (assignedVeh is not null) busyVehicles.Add(assignedVeh.Id);
        }

        foreach (var t in pendingTasks)
        {
            var best = FindBestCandidate(t, teams, request.Roads, busyTeams, busyVehicles, null, audit, ref auditOrder);
            if (best is null)
            {
                var reasons = ExplainUnassigned(t, teams, request.Roads, busyTeams, busyVehicles);
                Audit("task-unassigned", $"Task {t.Code} could not be assigned: {reasons}", t.Code);
                assignments.Add(new SolverAssignment(
                    t.Id, t.Code, null, null, null, null,
                    AllocationDecision.Unassigned, 0, 0, false, null, reasons, false, null, null));
                continue;
            }

            busyTeams.Add(best.Team.Id);
            busyVehicles.Add(best.Vehicle.Id);
            var meetsDeadline = !t.DeadlineMinutes.HasValue || best.Route!.TotalTravelMinutes <= t.DeadlineMinutes.Value;
            Audit("task-assigned",
                $"Task {t.Code} assigned to {best.Team.Code} with {best.Vehicle.Code}; travel={best.Route!.TotalTravelMinutes}m; deadlineMet={meetsDeadline}.",
                t.Code, best.Team.Code, best.Vehicle.Code);
            assignments.Add(new SolverAssignment(
                t.Id, t.Code, best.Team.Id, best.Team.Code,
                best.Vehicle.Id, best.Vehicle.Code,
                AllocationDecision.Assigned,
                best.Route.TotalTravelMinutes,
                best.Route.TotalTravelMinutes + t.DurationMinutes,
                meetsDeadline, best.Route, null, false, null, null));
        }

        bool feasible = assignments.All(a => a.Decision != AllocationDecision.Unassigned);

        var blockedStarted = assignments.Any(a =>
            a.Decision == AllocationDecision.Kept
            && a.Reason is not null
            && a.Reason.Contains("can no longer reach", StringComparison.Ordinal));

        if (blockedStarted)
            feasible = false;

        if (!feasible)
        {
            Audit("no-feasible-solution", "At least one task cannot be assigned under the current road and capability constraints.");
        }
        else
        {
            Audit("solve-complete", "All tasks assigned under current constraints.");
        }

        int cost = assignments.Sum(a => a.EstimatedTotalMinutes);
        return new SolverResult(feasible, cost,
            assignments.OrderBy(a => a.TaskCode, StringComparer.Ordinal).ToList(),
            audit, request.RoadSnapshotVersion);
    }

    private static Candidate? FindBestCandidate(
        SolverTask task,
        List<SolverTeam> teams,
        IReadOnlyList<SolverRoad> roads,
        HashSet<Guid> busyTeams,
        HashSet<Guid> busyVehicles,
        Guid? requireDifferent,
        List<SolverAuditEntry> audit,
        ref int auditOrder)
    {
        Candidate? best = null;

        foreach (var team in teams.OrderBy(x => x.Code, StringComparer.Ordinal))
        {
            if (!team.IsAvailable) continue;
            if (requireDifferent.HasValue && team.Id == requireDifferent.Value) continue;
            if (busyTeams.Contains(team.Id)) continue;

            var missing = task.RequiredCapabilities.Where(c => !team.Capabilities.Contains(c)).ToList();
            if (missing.Count > 0)
            {
                audit.Add(new SolverAuditEntry(++auditOrder, "candidate-reject",
                    task.Code, team.Code, null, null,
                    $"Team {team.Code} missing required capabilities: {string.Join(",", missing)}."));
                continue;
            }

            foreach (var vehicle in team.Vehicles.OrderBy(v => v.HeightMeters).ThenBy(v => v.Code, StringComparer.Ordinal))
            {
                if (!vehicle.IsAvailable || busyVehicles.Contains(vehicle.Id)) continue;

                var graph = new RoadGraph(roads, vehicle.HeightMeters);
                var route = graph.ShortestPath(team.BaseNodeId, task.LocationNodeId);
                if (route is null)
                {
                    audit.Add(new SolverAuditEntry(++auditOrder, "candidate-reject",
                        task.Code, team.Code, vehicle.Code, null,
                        $"No reachable route for {vehicle.Code} (height={vehicle.HeightMeters.ToString("F1", CultureInfo.InvariantCulture)}m) from {team.BaseNodeId} to {task.LocationNodeId}."));
                    continue;
                }

                bool meetsDeadline = !task.DeadlineMinutes.HasValue || route.TotalTravelMinutes <= task.DeadlineMinutes.Value;
                if (task.DeadlineMinutes.HasValue && !meetsDeadline)
                {
                    audit.Add(new SolverAuditEntry(++auditOrder, "candidate-reject",
                        task.Code, team.Code, vehicle.Code, null,
                        $"Route via {vehicle.Code} takes {route.TotalTravelMinutes}m, exceeding deadline {task.DeadlineMinutes.Value}m."));
                    continue;
                }

                var cand = new Candidate(team, vehicle, route, meetsDeadline);

                if (best is null || IsBetter(cand, best))
                    best = cand;
            }
        }

        return best;
    }

    private static bool IsBetter(Candidate a, Candidate b)
    {
        if (a.MeetsDeadline != b.MeetsDeadline) return a.MeetsDeadline;
        if (a.Route.TotalTravelMinutes != b.Route.TotalTravelMinutes)
            return a.Route.TotalTravelMinutes < b.Route.TotalTravelMinutes;
        if (a.Vehicle.HeightMeters != b.Vehicle.HeightMeters)
            return a.Vehicle.HeightMeters < b.Vehicle.HeightMeters;
        int c = string.CompareOrdinal(a.Team.Code, b.Team.Code);
        if (c != 0) return c < 0;
        return string.CompareOrdinal(a.Vehicle.Code, b.Vehicle.Code) < 0;
    }

    private static string ExplainUnassigned(
        SolverTask task,
        List<SolverTeam> teams,
        IReadOnlyList<SolverRoad> roads,
        HashSet<Guid> busyTeams,
        HashSet<Guid> busyVehicles)
    {
        var reasons = new List<string>();
        bool hadCapable = false;
        var closedRoads = roads.Where(r => !r.IsOpen).Select(r => r.Code).ToList();

        foreach (var team in teams)
        {
            if (busyTeams.Contains(team.Id))
            {
                reasons.Add($"team {team.Code} is already occupied");
                continue;
            }
            var missing = task.RequiredCapabilities.Where(c => !team.Capabilities.Contains(c)).ToList();
            if (missing.Count > 0)
            {
                reasons.Add($"team {team.Code} lacks {string.Join("/", missing)}");
                continue;
            }
            hadCapable = true;
            bool anyReachable = false;
            var vehicleIssues = new List<string>();
            foreach (var v in team.Vehicles)
            {
                if (!v.IsAvailable || busyVehicles.Contains(v.Id)) continue;
                var g = new RoadGraph(roads, v.HeightMeters);
                var route = g.ShortestPath(team.BaseNodeId, task.LocationNodeId);
                if (route is not null)
                {
                    anyReachable = true;
                    if (task.DeadlineMinutes.HasValue && route.TotalTravelMinutes > task.DeadlineMinutes.Value)
                        vehicleIssues.Add($"{v.Code} reaches in {route.TotalTravelMinutes}m > deadline {task.DeadlineMinutes.Value}m");
                }
                else
                {
                    var blockingRoads = roads
                        .Where(r => !r.IsOpen
                                    || (r.HeightLimitMeters.HasValue && v.HeightMeters > r.HeightLimitMeters.Value))
                        .Select(r => r.Code)
                        .ToList();
                    vehicleIssues.Add(blockingRoads.Count > 0
                        ? $"{v.Code} blocked by {string.Join("/", blockingRoads)}"
                        : $"{v.Code} has no path");
                }
            }
            if (!anyReachable)
                reasons.Add($"team {team.Code} unreachable ({string.Join("; ", vehicleIssues)})");
            else if (vehicleIssues.Count > 0)
                reasons.Add($"team {team.Code}: {string.Join("; ", vehicleIssues)}");
        }
        if (closedRoads.Count > 0)
            reasons.Insert(0, $"closed road(s): {string.Join(",", closedRoads)}");
        if (!hadCapable && reasons.Count == 0)
            reasons.Add("no team has the required capabilities");
        return string.Join("; ", reasons);
    }

    private sealed record Candidate(SolverTeam Team, SolverVehicle Vehicle, SolverRoute Route, bool MeetsDeadline);
}
