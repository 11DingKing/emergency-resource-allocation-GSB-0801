namespace EmergencyDispatch.Tests;

using EmergencyDispatch.Solver;

/// <summary>
/// A fully deterministic solver double. It returns a preconfigured <see cref="SolveResult"/>
/// (or one produced by a supplied function) regardless of the real algorithm, letting tests
/// assert the application service's idempotency, atomicity and concurrency behaviour in
/// isolation from allocation logic. It also records every input it received.
/// </summary>
public sealed class StubSolver : IAllocationSolver
{
    private readonly Func<SolveInput, SolveResult> _factory;

    public StubSolver(Func<SolveInput, SolveResult> factory) => _factory = factory;

    public StubSolver(SolveResult fixedResult) => _factory = _ => fixedResult with { };

    public List<SolveInput> Invocations { get; } = new();

    public int CallCount => Invocations.Count;

    public SolveResult Solve(SolveInput input)
    {
        Invocations.Add(input);
        var result = _factory(input);
        // Preserve the caller's input version so persistence keys line up.
        return result with { InputVersion = input.InputVersion };
    }

    /// <summary>An empty (all-unassigned-free) result: no assignments, no reasons, no audit.</summary>
    public static SolveResult Empty(string inputVersion) => new()
    {
        InputVersion = inputVersion,
        Assignments = Array.Empty<AssignmentPlan>(),
        Unassigned = Array.Empty<UnassignedPlan>(),
        Audit = Array.Empty<AuditRecord>(),
    };
}
