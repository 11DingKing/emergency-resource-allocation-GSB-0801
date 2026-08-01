namespace EmergencyAllocation.Core.Solving;

public sealed record SolverExplanation(
    ExplanationKind Kind,
    string RuleCode,
    string Message,
    string? RelatedTaskId = null,
    string? RelatedTeamId = null);
