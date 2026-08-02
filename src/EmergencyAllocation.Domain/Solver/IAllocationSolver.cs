namespace EmergencyAllocation.Domain.Solver;

public interface IAllocationSolver
{
    string Name { get; }

    SolverResult Solve(SolverRequest request);
}
