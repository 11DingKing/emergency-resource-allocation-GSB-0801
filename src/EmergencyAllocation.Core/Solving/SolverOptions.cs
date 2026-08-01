namespace EmergencyAllocation.Core.Solving;

public sealed record SolverOptions(
    bool DangerLevelRaised,
    bool AllowPreemption,
    string SolverVersion);
