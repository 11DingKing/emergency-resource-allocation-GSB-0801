namespace EmergencyDispatch.Solver;

/// <summary>
/// The single boundary between the allocation algorithm and everything else. The API and
/// persistence layers depend only on this interface, never on a concrete solver, so tests
/// can inject a deterministic double. Implementations must be pure functions of their
/// input: identical <see cref="SolveInput"/> yields an identical <see cref="SolveResult"/>,
/// including tie-break ordering.
/// </summary>
public interface IAllocationSolver
{
    SolveResult Solve(SolveInput input);
}
