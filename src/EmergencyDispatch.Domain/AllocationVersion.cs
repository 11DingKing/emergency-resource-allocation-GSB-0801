namespace EmergencyDispatch.Domain;

/// <summary>
/// An immutable, persisted result of one solve. Keyed by <see cref="InputVersion"/> so
/// that re-submitting the same input version is idempotent: the stored version is
/// returned rather than re-solved. A version is only ever written as a whole inside a
/// single serializable transaction, so external readers never observe a half-applied plan.
/// </summary>
public class AllocationVersion
{
    public Guid Id { get; set; }

    /// <summary>
    /// Monotonic version number of this allocation record (1, 2, 3 ...). Distinct from
    /// <see cref="InputVersion"/>, which identifies the request that produced it.
    /// </summary>
    public int VersionNumber { get; set; }

    /// <summary>
    /// Client-supplied idempotency key describing the input snapshot. Uniquely indexed:
    /// a duplicate submit returns the existing version instead of creating a new one.
    /// </summary>
    public string InputVersion { get; set; } = string.Empty;

    /// <summary>
    /// Deterministic digest of the world snapshot (roads, tasks, teams, vehicles, routes)
    /// this version was solved against. A repeated <see cref="InputVersion"/> is only a valid
    /// idempotent replay when it carries the same <see cref="SnapshotVersion"/>; a mismatch is
    /// a conflict, never a stale replay.
    /// </summary>
    public string SnapshotVersion { get; set; } = string.Empty;

    /// <summary>
    /// Canonical JSON of the bound snapshot, retained so a later conflicting submit can be
    /// diffed field-by-field against the plan that is already on record.
    /// </summary>
    public string SnapshotJson { get; set; } = string.Empty;

    public AllocationKind Kind { get; set; }

    /// <summary>Total plan cost in minutes (sum of arrival times of assigned tasks).</summary>
    public int TotalCostMinutes { get; set; }

    /// <summary>True when the solver produced no feasible full plan for at least one task.</summary>
    public bool HasUnassignedTasks { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<Assignment> Assignments { get; set; } = new();

    public List<UnassignedReason> UnassignedReasons { get; set; } = new();

    public List<AuditEntry> AuditEntries { get; set; } = new();

    /// <summary>Danger level captured per task at the moment this version was produced.</summary>
    public List<AllocationBaseline> Baselines { get; set; } = new();
}

/// <summary>
/// Snapshot of a task's danger level at the time a version was produced. A replan compares
/// the current task danger against the latest baseline to decide whether danger has escalated
/// (the sole trigger that may justify preempting an in-progress task).
/// </summary>
public class AllocationBaseline
{
    public Guid Id { get; set; }

    public Guid AllocationVersionId { get; set; }

    public Guid TaskId { get; set; }

    public int DangerLevel { get; set; }
}

/// <summary>One team+vehicle assigned to one task within an allocation version.</summary>
public class Assignment
{
    public Guid Id { get; set; }

    public Guid AllocationVersionId { get; set; }

    public Guid TaskId { get; set; }

    public Guid TeamId { get; set; }

    public Guid VehicleId { get; set; }

    public Guid RoadSegmentId { get; set; }

    /// <summary>Arrival time in minutes = route travel time. Must be within the task deadline.</summary>
    public int ArrivalMinutes { get; set; }

    /// <summary>Denormalised stable codes for stable explanation output.</summary>
    public string TaskCode { get; set; } = string.Empty;
    public string TeamCode { get; set; } = string.Empty;
    public string VehicleCode { get; set; } = string.Empty;
    public string RoadSegmentCode { get; set; } = string.Empty;
}

/// <summary>Records why a specific task could not be assigned in this version.</summary>
public class UnassignedReason
{
    public Guid Id { get; set; }

    public Guid AllocationVersionId { get; set; }

    public Guid TaskId { get; set; }

    public string TaskCode { get; set; } = string.Empty;

    /// <summary>Stable reason code from <see cref="ReasonCodes"/>.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Human-readable detail; deterministic given identical input.</summary>
    public string Detail { get; set; } = string.Empty;
}

/// <summary>
/// One auditable decision within a version. Every assignment, hold and non-assignment is
/// justified by exactly one <see cref="RuleCode"/> so the explanation is fully traceable.
/// </summary>
public class AuditEntry
{
    public Guid Id { get; set; }

    public Guid AllocationVersionId { get; set; }

    /// <summary>Order within the version; deterministic for a given input.</summary>
    public int Sequence { get; set; }

    public Guid? TaskId { get; set; }

    public string TaskCode { get; set; } = string.Empty;

    /// <summary>Stable rule code from <see cref="RuleCodes"/>.</summary>
    public string RuleCode { get; set; } = string.Empty;

    /// <summary>Deterministic human-readable justification.</summary>
    public string Message { get; set; } = string.Empty;
}
