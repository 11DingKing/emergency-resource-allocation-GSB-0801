namespace EmergencyAllocation.Core.Solving;

public sealed class SolverResult
{
    public required bool IsFeasible { get; init; }
    public string? NoFeasibleReason { get; init; }
    public required IReadOnlyList<AssignmentDecision> Assignments { get; init; }
    public required IReadOnlyList<SolverExplanation> Explanations { get; init; }
    public required long TotalCost { get; init; }
    public required string SolverVersion { get; init; }
}
