namespace EmergencyDispatch.Infrastructure;

using EmergencyDispatch.Domain;

/// <summary>
/// Coordinates a solve request: read a consistent snapshot, invoke the injected solver,
/// and persist the resulting <see cref="AllocationVersion"/> as one atomic unit. The
/// concrete algorithm is never referenced here — only <c>IAllocationSolver</c> — so the
/// dispatch algorithm lives entirely outside both the API and this persistence coordinator.
/// </summary>
public interface IAllocationService
{
    /// <summary>
    /// Produce (or return the existing) allocation for <paramref name="request"/>. Idempotent
    /// on <see cref="SolveRequest.InputVersion"/>: a repeated input version returns the stored
    /// version untouched. The whole version is committed in a single serializable transaction,
    /// with bounded retries on serialization/commit failure, so callers never observe a
    /// half-written plan.
    /// </summary>
    Task<AllocationResult> SolveAsync(SolveRequest request, CancellationToken ct = default);

    Task<AllocationVersion?> GetVersionAsync(int versionNumber, CancellationToken ct = default);

    Task<AllocationVersion?> GetByInputVersionAsync(string inputVersion, CancellationToken ct = default);

    Task<AllocationVersion?> GetLatestAsync(CancellationToken ct = default);

    Task<AllocationExplanation?> ExplainAsync(int versionNumber, CancellationToken ct = default);
}

/// <summary>A request to solve or replan, carrying the idempotency key.</summary>
public sealed record SolveRequest
{
    /// <summary>Client-supplied idempotency key describing the input snapshot.</summary>
    public required string InputVersion { get; init; }

    /// <summary>True to allow danger-escalated preemption of in-progress tasks.</summary>
    public bool IsReplan { get; init; }
}

/// <summary>Outcome of a solve, including whether it was served from the idempotency cache.</summary>
public sealed record AllocationResult
{
    public required AllocationVersion Version { get; init; }

    /// <summary>True when an existing version was returned instead of a fresh solve.</summary>
    public required bool WasExisting { get; init; }
}

/// <summary>A structured, human-readable explanation of one allocation version.</summary>
public sealed record AllocationExplanation
{
    public required int VersionNumber { get; init; }
    public required string InputVersion { get; init; }
    public required AllocationKind Kind { get; init; }
    public required int TotalCostMinutes { get; init; }
    public required IReadOnlyList<AuditEntry> Audit { get; init; }
    public required IReadOnlyList<Assignment> Assignments { get; init; }
    public required IReadOnlyList<UnassignedReason> Unassigned { get; init; }
}
