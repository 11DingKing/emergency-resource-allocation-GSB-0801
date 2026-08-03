namespace EmergencyAllocation.Core;

public enum ExplanationKind
{
    Rule = 0,
    Decision = 1,
    Warning = 2,
    NoFeasibleSolution = 3,
    Preemption = 4,
    TieBreak = 5,
    RoadBlocked = 6
}
