namespace EmergencyAllocation.Core.Solving;

public static class RuleCodes
{
    public const string CapabilityMatch = "RULE_CAPABILITY_MATCH";
    public const string HeightLimit = "RULE_HEIGHT_LIMIT";
    public const string ArrivalDeadline = "RULE_ARRIVAL_DEADLINE";
    public const string NonPreemptive = "RULE_NON_PREEMPTIVE";
    public const string PreemptionAllowed = "RULE_PREEMPTION_ALLOWED";
    public const string PreemptionDenied = "RULE_PREEMPTION_DENIED";
    public const string RoadClosed = "RULE_ROAD_CLOSED";
    public const string TieBreak = "RULE_TIE_BREAK";
    public const string LifeSafetyMustAssign = "RULE_LIFE_SAFETY_MUST_ASSIGN";
    public const string NoFeasibleRoute = "RULE_NO_FEASIBLE_ROUTE";
    public const string InputVersionIdempotent = "RULE_INPUT_VERSION_IDEMPOTENT";
}
