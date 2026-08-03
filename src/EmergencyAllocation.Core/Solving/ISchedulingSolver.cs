namespace EmergencyAllocation.Core.Solving;

public interface ISchedulingSolver
{
    string Version { get; }

    Task<SolverResult> SolveAsync(SchedulingProblem problem, CancellationToken cancellationToken = default);
}
