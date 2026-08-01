namespace EmergencyDispatch.Domain;

/// <summary>
/// Stable capability identifiers. Capabilities are modelled as a stable string set
/// (never numeric enums) so that persisted allocations, audit records and the solver
/// agree on identity across versions.
/// </summary>
public static class Capabilities
{
    public const string WaterRescue = "water-rescue";
    public const string SlopeInspection = "slope-inspection";
    public const string FirstAid = "first-aid";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        {
            WaterRescue,
            SlopeInspection,
            FirstAid,
        };
}

/// <summary>
/// Stable, ordered danger-level names for life-safety severity. Persisted and exposed as
/// stable strings; internally each maps to a monotonic rank so the solver can compare
/// escalation deterministically. A rise in rank is the sole trigger that may justify
/// preempting an in-progress task.
/// </summary>
public static class DangerLevels
{
    public const string Routine = "routine";
    public const string Elevated = "elevated";
    public const string High = "high";
    public const string Critical = "critical";

    // Ordered from least to most severe; index + 1 is the rank stored on tasks.
    private static readonly string[] Ordered = { Routine, Elevated, High, Critical };

    /// <summary>Monotonic rank for a stable name (routine=1 … critical=4).</summary>
    public static int Rank(string name)
    {
        var idx = Array.IndexOf(Ordered, name);
        if (idx < 0) throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown danger level.");
        return idx + 1;
    }

    /// <summary>Stable name for a rank; clamps to the nearest defined level.</summary>
    public static string Name(int rank)
    {
        var idx = Math.Clamp(rank - 1, 0, Ordered.Length - 1);
        return Ordered[idx];
    }

    public static bool IsDefined(string name) => Array.IndexOf(Ordered, name) >= 0;
}

/// <summary>
/// Stable reason codes explaining why a task could not be assigned. Emitted by the
/// solver and persisted verbatim so the explanation API is byte-for-byte reproducible.
/// </summary>
public static class ReasonCodes
{
    public const string NoCapableTeam = "NO_CAPABLE_TEAM";
    public const string NoVehicleAvailable = "NO_VEHICLE_AVAILABLE";
    public const string NoFeasibleRoute = "NO_FEASIBLE_ROUTE";
    public const string DeadlineExceeded = "DEADLINE_EXCEEDED";
    public const string TeamBusy = "TEAM_BUSY";
    public const string PreemptionNotAllowed = "PREEMPTION_NOT_ALLOWED";
}

/// <summary>
/// Stable rule codes attached to every audit entry. Each assignment decision or
/// non-decision is justified by exactly one rule code so a human can trace the plan.
/// </summary>
public static class RuleCodes
{
    public const string InitialAssignment = "INITIAL_ASSIGNMENT";
    public const string DeterministicTieBreak = "DETERMINISTIC_TIEBREAK";
    public const string RerouteAfterRoadCut = "REROUTE_AFTER_ROAD_CUT";
    public const string VehicleReassigned = "VEHICLE_REASSIGNED";
    public const string TeamReassigned = "TEAM_REASSIGNED";
    public const string NonPreemptionHeld = "NON_PREEMPTION_HELD";
    public const string ReplanDangerEscalated = "REPLAN_DANGER_ESCALATED";
    public const string Unassigned = "UNASSIGNED";
    public const string InProgressLocked = "IN_PROGRESS_LOCKED";
}
