namespace EmergencyDispatch.Solver;

using EmergencyDispatch.Domain;

/// <summary>
/// Deterministic greedy allocation solver. It is a pure function of its
/// <see cref="SolveInput"/>: no clock, no randomness, no I/O. Given identical input it
/// always returns the identical <see cref="SolveResult"/>, including tie-break ordering,
/// unassigned reasons and audit trail. The algorithm:
///
/// 1. In-progress tasks are pinned first and are non-preemptable by default; their team
///    and vehicle are removed from the available pool (rule NON_PREEMPTION_HELD).
/// 2. Remaining tasks are considered in a stable priority order: highest danger first,
///    then earliest deadline, then task code.
/// 3. For each task the solver enumerates every feasible (team, vehicle, road) triple —
///    team has all required capabilities, road is open, vehicle fits the height limit and
///    arrival is within the deadline — and picks the minimum-arrival triple. Ties are
///    broken deterministically by (teamCode, vehicleCode, roadCode) and flagged with rule
///    DETERMINISTIC_TIEBREAK so the choice is auditable.
/// 4. If no triple is feasible, the most specific unassigned reason is recorded.
/// 5. An in-progress task may only be preempted during a replan when this task's danger
///    level has risen AND a free alternative team can fully cover the preempted task; the
///    reason is recorded as rule REPLAN_DANGER_ESCALATED.
/// </summary>
public sealed class GreedyAllocationSolver : IAllocationSolver
{
    public SolveResult Solve(SolveInput input)
    {
        var state = new SolveState(input);

        PinInProgressTasks(state);
        AssignPendingTasks(state);

        return new SolveResult
        {
            InputVersion = input.InputVersion,
            Assignments = state.Assignments,
            Unassigned = state.Unassigned,
            Audit = state.Audit,
        };
    }

    private static void PinInProgressTasks(SolveState state)
    {
        var inProgress = state.Input.Tasks
            .Where(t => t.IsInProgress)
            .OrderBy(t => t.Code, StringComparer.Ordinal);

        foreach (var task in inProgress)
        {
            var team = state.Input.Teams.FirstOrDefault(t => t.Id == task.ExecutingTeamId);
            var vehicle = state.Input.Vehicles.FirstOrDefault(v => v.Id == task.ExecutingVehicleId);
            if (team is null || vehicle is null)
            {
                // Defensive: an in-progress task without a resolvable crew is left unassigned.
                state.AddUnassigned(task, ReasonCodes.TeamBusy,
                    "In-progress task references an unknown team or vehicle.");
                continue;
            }

            state.MarkUsed(team.Id, vehicle.Id);
            state.AddAssignment(new AssignmentPlan
            {
                TaskId = task.Id,
                TaskCode = task.Code,
                TeamId = team.Id,
                TeamCode = team.Code,
                VehicleId = vehicle.Id,
                VehicleCode = vehicle.Code,
                RoadSegmentId = Guid.Empty,
                RoadSegmentCode = "ON-SITE",
                ArrivalMinutes = 0,
            });
            state.AddAudit(task, RuleCodes.NonPreemptionHeld,
                $"Task {task.Code} is in progress and held with team {team.Code}; not preemptable.");
        }
    }

    private static void AssignPendingTasks(SolveState state)
    {
        var pending = state.Input.Tasks
            .Where(t => !t.IsInProgress)
            .OrderByDescending(t => t.DangerLevel)
            .ThenBy(t => t.DeadlineMinutes)
            .ThenBy(t => t.Code, StringComparer.Ordinal);

        foreach (var task in pending)
        {
            TryAssign(state, task);
        }
    }

    private static void TryAssign(SolveState state, TaskSnapshot task)
    {
        var capableTeams = state.Input.Teams
            .Where(t => task.RequiredCapabilities.All(t.Capabilities.Contains))
            .ToList();

        if (capableTeams.Count == 0)
        {
            state.AddUnassigned(task, ReasonCodes.NoCapableTeam,
                $"No team possesses all required capabilities: {FormatCaps(task.RequiredCapabilities)}.");
            state.AddAudit(task, RuleCodes.Unassigned,
                $"Task {task.Code} unassigned: {ReasonCodes.NoCapableTeam}.");
            return;
        }

        var candidates = EnumerateCandidates(state, task, capableTeams);

        if (candidates.Count > 0)
        {
            // Minimum arrival; deterministic tie-break on (teamCode, vehicleCode, roadCode).
            candidates.Sort(CandidateComparer.Instance);
            var best = candidates[0];
            var minArrival = best.ArrivalMinutes;
            var tied = candidates.Count(c => c.ArrivalMinutes == minArrival) > 1;

            state.MarkUsed(best.TeamId, best.VehicleId);
            state.AddAssignment(new AssignmentPlan
            {
                TaskId = task.Id,
                TaskCode = task.Code,
                TeamId = best.TeamId,
                TeamCode = best.TeamCode,
                VehicleId = best.VehicleId,
                VehicleCode = best.VehicleCode,
                RoadSegmentId = best.RoadSegmentId,
                RoadSegmentCode = best.RoadSegmentCode,
                ArrivalMinutes = best.ArrivalMinutes,
            });

            if (tied)
            {
                state.AddAudit(task, RuleCodes.DeterministicTieBreak,
                    $"Task {task.Code} had equal-cost options at {minArrival} min; " +
                    $"deterministic tie-break selected team {best.TeamCode}, vehicle {best.VehicleCode}, road {best.RoadSegmentCode}.");
            }
            else
            {
                state.AddAudit(task, RuleCodes.InitialAssignment,
                    $"Task {task.Code} assigned to team {best.TeamCode} with vehicle {best.VehicleCode} via road {best.RoadSegmentCode}, arriving in {best.ArrivalMinutes} min.");
            }
            return;
        }

        // No feasible triple with currently free resources. Consider a danger-escalated
        // preemption during a replan before giving up.
        if (TryPreempt(state, task, capableTeams))
        {
            return;
        }

        RecordUnassignedReason(state, task, capableTeams);
    }

    /// <summary>
    /// Enumerate feasible (team, vehicle, road) triples over the currently free resources,
    /// in a stable order so the resulting list — and therefore the tie-break — is deterministic.
    /// </summary>
    private static List<Candidate> EnumerateCandidates(
        SolveState state, TaskSnapshot task, IEnumerable<TeamSnapshot> capableTeams)
    {
        var teams = capableTeams
            .Where(t => !state.IsTeamUsed(t.Id))
            .OrderBy(t => t.Code, StringComparer.Ordinal);

        var vehicles = state.Input.Vehicles
            .Where(v => !state.IsVehicleUsed(v.Id))
            .OrderBy(v => v.Code, StringComparer.Ordinal)
            .ToList();

        var routes = state.RoutesForTask(task.Id);

        var result = new List<Candidate>();
        foreach (var team in teams)
        {
            foreach (var vehicle in vehicles)
            {
                foreach (var (route, road) in routes)
                {
                    if (!road.IsOpen) continue;
                    if (vehicle.HeightMeters > road.HeightLimitMeters) continue;
                    if (route.TravelMinutes > task.DeadlineMinutes) continue;

                    result.Add(new Candidate
                    {
                        TeamId = team.Id,
                        TeamCode = team.Code,
                        VehicleId = vehicle.Id,
                        VehicleCode = vehicle.Code,
                        RoadSegmentId = road.Id,
                        RoadSegmentCode = road.Code,
                        ArrivalMinutes = route.TravelMinutes,
                    });
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Attempt to free a capable team from an in-progress task so it can serve
    /// <paramref name="task"/>. Permitted only when this is a replan, the task's danger
    /// level has risen, and a free alternative team can fully cover the preempted task.
    /// </summary>
    private static bool TryPreempt(SolveState state, TaskSnapshot task, List<TeamSnapshot> capableTeams)
    {
        if (!state.Input.IsReplan) return false;

        var escalated = task.PreviousDangerLevel is int prev && task.DangerLevel > prev;
        if (!escalated) return false;

        // In-progress tasks whose executing team is capable of THIS task.
        var lockedTasks = state.Input.Tasks
            .Where(t => t.IsInProgress && t.ExecutingTeamId is not null)
            .Where(t => capableTeams.Any(ct => ct.Id == t.ExecutingTeamId))
            .OrderBy(t => t.Code, StringComparer.Ordinal)
            .ToList();

        foreach (var locked in lockedTasks)
        {
            var incumbentTeamId = locked.ExecutingTeamId!.Value;

            // A free alternative team (other than the incumbent) that can fully cover the
            // preempted task's capabilities.
            var replacement = state.Input.Teams
                .Where(t => t.Id != incumbentTeamId)
                .Where(t => !state.IsTeamUsed(t.Id))
                .Where(t => locked.RequiredCapabilities.All(t.Capabilities.Contains))
                .OrderBy(t => t.Code, StringComparer.Ordinal)
                .FirstOrDefault();

            if (replacement is null) continue;

            // Free the incumbent team+vehicle, reassign the preempted task to the replacement.
            state.ReleaseTeam(incumbentTeamId);
            var incumbentVehicleId = locked.ExecutingVehicleId;
            if (incumbentVehicleId is Guid vid) state.ReleaseVehicle(vid);

            state.RemoveAssignment(locked.Id);

            // Cover the preempted task with the replacement team on-site (it takes over).
            var replacementVehicle = state.Input.Vehicles
                .FirstOrDefault(v => v.Id == locked.ExecutingVehicleId) ??
                state.Input.Vehicles
                    .Where(v => !state.IsVehicleUsed(v.Id))
                    .OrderBy(v => v.Code, StringComparer.Ordinal)
                    .FirstOrDefault();

            if (replacementVehicle is null)
            {
                // Cannot cover the preempted task; undo and refuse preemption.
                state.MarkUsed(incumbentTeamId, incumbentVehicleId ?? Guid.Empty);
                state.AddAssignment(RebuildHeld(locked, state));
                continue;
            }

            state.MarkUsed(replacement.Id, replacementVehicle.Id);
            state.AddAssignment(new AssignmentPlan
            {
                TaskId = locked.Id,
                TaskCode = locked.Code,
                TeamId = replacement.Id,
                TeamCode = replacement.Code,
                VehicleId = replacementVehicle.Id,
                VehicleCode = replacementVehicle.Code,
                RoadSegmentId = Guid.Empty,
                RoadSegmentCode = "ON-SITE",
                ArrivalMinutes = 0,
            });
            state.AddAudit(locked, RuleCodes.TeamReassigned,
                $"Preempted task {locked.Code} reassigned from team " +
                $"{TeamCode(state, incumbentTeamId)} to alternative team {replacement.Code}.");

            // Now assign THIS task with the freed resources.
            var candidates = EnumerateCandidates(state, task, capableTeams);
            if (candidates.Count == 0)
            {
                // Freed team still cannot serve the task; record and stop.
                RecordUnassignedReason(state, task, capableTeams);
                return true;
            }

            candidates.Sort(CandidateComparer.Instance);
            var best = candidates[0];
            state.MarkUsed(best.TeamId, best.VehicleId);
            state.AddAssignment(new AssignmentPlan
            {
                TaskId = task.Id,
                TaskCode = task.Code,
                TeamId = best.TeamId,
                TeamCode = best.TeamCode,
                VehicleId = best.VehicleId,
                VehicleCode = best.VehicleCode,
                RoadSegmentId = best.RoadSegmentId,
                RoadSegmentCode = best.RoadSegmentCode,
                ArrivalMinutes = best.ArrivalMinutes,
            });
            state.AddAudit(task, RuleCodes.ReplanDangerEscalated,
                $"Task {task.Code} danger escalated from {task.PreviousDangerLevel} to {task.DangerLevel}; " +
                $"team {best.TeamCode} freed via preemption and assigned, arriving in {best.ArrivalMinutes} min.");
            return true;
        }

        return false;
    }

    private static AssignmentPlan RebuildHeld(TaskSnapshot locked, SolveState state)
    {
        var team = state.Input.Teams.First(t => t.Id == locked.ExecutingTeamId);
        var vehicle = state.Input.Vehicles.First(v => v.Id == locked.ExecutingVehicleId);
        return new AssignmentPlan
        {
            TaskId = locked.Id,
            TaskCode = locked.Code,
            TeamId = team.Id,
            TeamCode = team.Code,
            VehicleId = vehicle.Id,
            VehicleCode = vehicle.Code,
            RoadSegmentId = Guid.Empty,
            RoadSegmentCode = "ON-SITE",
            ArrivalMinutes = 0,
        };
    }

    private static void RecordUnassignedReason(
        SolveState state, TaskSnapshot task, List<TeamSnapshot> capableTeams)
    {
        var freeCapable = capableTeams.Where(t => !state.IsTeamUsed(t.Id)).ToList();
        if (freeCapable.Count == 0)
        {
            // Every capable team is occupied. Distinguish an in-progress lock (potential
            // preemption target) from a team busy on another pending task.
            var lockedByInProgress = capableTeams.Any(ct =>
                state.Input.Tasks.Any(t => t.IsInProgress && t.ExecutingTeamId == ct.Id));
            if (lockedByInProgress)
            {
                state.AddUnassigned(task, ReasonCodes.PreemptionNotAllowed,
                    "All capable teams are executing protected in-progress tasks and preemption conditions are not met.");
            }
            else
            {
                state.AddUnassigned(task, ReasonCodes.TeamBusy,
                    "All capable teams are already committed to higher-priority tasks in this plan.");
            }
            state.AddAudit(task, RuleCodes.Unassigned, $"Task {task.Code} unassigned.");
            return;
        }

        var freeVehicles = state.Input.Vehicles.Where(v => !state.IsVehicleUsed(v.Id)).ToList();
        if (freeVehicles.Count == 0)
        {
            state.AddUnassigned(task, ReasonCodes.NoVehicleAvailable,
                "No vehicle is available in the shared pool for this task.");
            state.AddAudit(task, RuleCodes.Unassigned, $"Task {task.Code} unassigned.");
            return;
        }

        var routes = state.RoutesForTask(task.Id);
        var openRoutes = routes.Where(r => r.road.IsOpen).ToList();
        if (openRoutes.Count == 0)
        {
            state.AddUnassigned(task, ReasonCodes.NoFeasibleRoute,
                "No open road segment reaches this task; all candidate roads are cut.");
            state.AddAudit(task, RuleCodes.Unassigned, $"Task {task.Code} unassigned.");
            return;
        }

        var fitRoutes = openRoutes
            .Where(r => freeVehicles.Any(v => v.HeightMeters <= r.road.HeightLimitMeters))
            .ToList();
        if (fitRoutes.Count == 0)
        {
            state.AddUnassigned(task, ReasonCodes.NoFeasibleRoute,
                "No available vehicle fits the height limit of any open road to this task.");
            state.AddAudit(task, RuleCodes.Unassigned, $"Task {task.Code} unassigned.");
            return;
        }

        // Roads reachable and a vehicle fits, but every such route breaches the deadline.
        state.AddUnassigned(task, ReasonCodes.DeadlineExceeded,
            $"The fastest feasible route exceeds the {task.DeadlineMinutes}-minute deadline.");
        state.AddAudit(task, RuleCodes.Unassigned, $"Task {task.Code} unassigned.");
    }

    private static string TeamCode(SolveState state, Guid teamId) =>
        state.Input.Teams.FirstOrDefault(t => t.Id == teamId)?.Code ?? teamId.ToString();

    private static string FormatCaps(IEnumerable<string> caps) =>
        string.Join(", ", caps.OrderBy(c => c, StringComparer.Ordinal));

    private sealed class Candidate
    {
        public required Guid TeamId { get; init; }
        public required string TeamCode { get; init; }
        public required Guid VehicleId { get; init; }
        public required string VehicleCode { get; init; }
        public required Guid RoadSegmentId { get; init; }
        public required string RoadSegmentCode { get; init; }
        public required int ArrivalMinutes { get; init; }
    }

    /// <summary>Total order: arrival asc, then team, vehicle, road codes ascending (ordinal).</summary>
    private sealed class CandidateComparer : IComparer<Candidate>
    {
        public static readonly CandidateComparer Instance = new();

        public int Compare(Candidate? x, Candidate? y)
        {
            ArgumentNullException.ThrowIfNull(x);
            ArgumentNullException.ThrowIfNull(y);
            var c = x.ArrivalMinutes.CompareTo(y.ArrivalMinutes);
            if (c != 0) return c;
            c = string.CompareOrdinal(x.TeamCode, y.TeamCode);
            if (c != 0) return c;
            c = string.CompareOrdinal(x.VehicleCode, y.VehicleCode);
            if (c != 0) return c;
            return string.CompareOrdinal(x.RoadSegmentCode, y.RoadSegmentCode);
        }
    }

    /// <summary>Mutable bookkeeping for one solve. Never escapes the solver.</summary>
    private sealed class SolveState
    {
        private readonly HashSet<Guid> _usedTeams = new();
        private readonly HashSet<Guid> _usedVehicles = new();
        private readonly Dictionary<Guid, RoadSnapshot> _roadsById;
        private readonly ILookup<Guid, RouteSnapshot> _routesByTask;
        private int _auditSeq;

        public SolveState(SolveInput input)
        {
            Input = input;
            _roadsById = input.Roads.ToDictionary(r => r.Id);
            _routesByTask = input.Routes.ToLookup(r => r.TaskId);
        }

        public SolveInput Input { get; }
        public List<AssignmentPlan> Assignments { get; } = new();
        public List<UnassignedPlan> Unassigned { get; } = new();
        public List<AuditRecord> Audit { get; } = new();

        public bool IsTeamUsed(Guid id) => _usedTeams.Contains(id);
        public bool IsVehicleUsed(Guid id) => _usedVehicles.Contains(id);
        public void MarkUsed(Guid teamId, Guid vehicleId)
        {
            _usedTeams.Add(teamId);
            if (vehicleId != Guid.Empty) _usedVehicles.Add(vehicleId);
        }
        public void ReleaseTeam(Guid teamId) => _usedTeams.Remove(teamId);
        public void ReleaseVehicle(Guid vehicleId) => _usedVehicles.Remove(vehicleId);

        public IReadOnlyList<(RouteSnapshot route, RoadSnapshot road)> RoutesForTask(Guid taskId) =>
            _routesByTask[taskId]
                .Where(r => _roadsById.ContainsKey(r.RoadSegmentId))
                .Select(r => (r, _roadsById[r.RoadSegmentId]))
                .OrderBy(t => t.Item2.Code, StringComparer.Ordinal)
                .ToList();

        public void AddAssignment(AssignmentPlan plan) => Assignments.Add(plan);
        public void RemoveAssignment(Guid taskId) => Assignments.RemoveAll(a => a.TaskId == taskId);

        public void AddUnassigned(TaskSnapshot task, string code, string detail) =>
            Unassigned.Add(new UnassignedPlan
            {
                TaskId = task.Id,
                TaskCode = task.Code,
                Code = code,
                Detail = detail,
            });

        public void AddAudit(TaskSnapshot task, string rule, string message) =>
            Audit.Add(new AuditRecord
            {
                Sequence = _auditSeq++,
                TaskId = task.Id,
                TaskCode = task.Code,
                RuleCode = rule,
                Message = message,
            });
    }
}
