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
    /// Produce (or return the existing) allocation for <paramref name="request"/>. The request
    /// binds to a snapshot via <see cref="SolveRequest.SnapshotVersion"/>:
    /// <list type="bullet">
    /// <item>Same <c>inputVersion</c> + same snapshot digest ⇒ idempotent replay of the stored plan.</item>
    /// <item>Same <c>inputVersion</c> + different digest ⇒ a <see cref="AllocationResult.Conflict"/>
    /// with a field-level diff; the old plan is never replayed under a changed world.</item>
    /// <item>New <c>inputVersion</c> ⇒ solved and persisted in one serializable transaction,
    /// with bounded retries so callers never observe a half-written plan.</item>
    /// </list>
    /// </summary>
    Task<AllocationResult> SolveAsync(SolveRequest request, CancellationToken ct = default);

    /// <summary>Build the current world snapshot and its deterministic digest.</summary>
    Task<WorldSnapshot> GetCurrentSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// Record a road status change (close/reopen) idempotently on <paramref name="eventId"/>
    /// and apply it to the road's current state. Returns the resulting snapshot digest.
    /// </summary>
    Task<RoadEventResult> RecordRoadEventAsync(
        string eventId, string roadCode, bool closed, CancellationToken ct = default);

    /// <summary>Update a task's danger level to a stable danger-level name (e.g. "critical").</summary>
    Task<bool> SetTaskDangerAsync(string taskCode, string dangerLevel, CancellationToken ct = default);

    Task<AllocationVersion?> GetVersionAsync(int versionNumber, CancellationToken ct = default);

    Task<AllocationVersion?> GetByInputVersionAsync(string inputVersion, CancellationToken ct = default);

    Task<AllocationVersion?> GetLatestAsync(CancellationToken ct = default);

    Task<AllocationExplanation?> ExplainAsync(int versionNumber, CancellationToken ct = default);
}

/// <summary>A request to solve or replan, bound to a specific world snapshot.</summary>
public sealed record SolveRequest
{
    /// <summary>Client-supplied idempotency key describing the request.</summary>
    public required string InputVersion { get; init; }

    /// <summary>
    /// Digest of the world snapshot the caller solved against (from <c>GET /api/snapshot</c>).
    /// Required: a request must bind to a real snapshot version so replays and conflicts can
    /// be distinguished.
    /// </summary>
    public required string SnapshotVersion { get; init; }

    /// <summary>True to allow danger-escalated preemption of in-progress tasks.</summary>
    public bool IsReplan { get; init; }
}

/// <summary>
/// Outcome of a solve. Exactly one of <see cref="Version"/> (success/replay) or
/// <see cref="Conflict"/> (snapshot mismatch) is populated.
/// </summary>
public sealed record AllocationResult
{
    /// <summary>The persisted or replayed version; null when this is a conflict.</summary>
    public AllocationVersion? Version { get; init; }

    /// <summary>True when an existing version was replayed instead of a fresh solve.</summary>
    public bool WasExisting { get; init; }

    /// <summary>Populated when the same input version was submitted against a different snapshot.</summary>
    public SnapshotConflict? Conflict { get; init; }

    public bool IsConflict => Conflict is not null;
}

/// <summary>
/// Describes a rejected solve where the input version already exists but was bound to a
/// different snapshot. Carries the field-level diff so the caller can see what moved.
/// </summary>
public sealed record SnapshotConflict
{
    public required string InputVersion { get; init; }
    public required string RequestedSnapshotVersion { get; init; }
    public required string StoredSnapshotVersion { get; init; }
    public required SnapshotDiff Diff { get; init; }
    public required IReadOnlyList<RoadEventRef> RoadEvents { get; init; }
    public required string Message { get; init; }
}

/// <summary>A road event referenced by an explanation or conflict.</summary>
public sealed record RoadEventRef
{
    public required string EventId { get; init; }
    public required string RoadCode { get; init; }
    public required bool Closed { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
}

/// <summary>Result of recording a road event.</summary>
public sealed record RoadEventResult
{
    public required string EventId { get; init; }
    public required string RoadCode { get; init; }
    public required bool Closed { get; init; }
    public required bool WasExisting { get; init; }
    public required string SnapshotVersion { get; init; }
}

/// <summary>A structured, human-readable explanation of one allocation version.</summary>
public sealed record AllocationExplanation
{
    public required int VersionNumber { get; init; }
    public required string InputVersion { get; init; }
    public required string SnapshotVersion { get; init; }
    public required AllocationKind Kind { get; init; }
    public required int TotalCostMinutes { get; init; }
    public required IReadOnlyList<AuditEntry> Audit { get; init; }
    public required IReadOnlyList<Assignment> Assignments { get; init; }
    public required IReadOnlyList<UnassignedReason> Unassigned { get; init; }
    public required IReadOnlyList<RoadEventRef> RoadEvents { get; init; }
}
