namespace EmergencyDispatch.Solver;

/// <summary>
/// Immutable output of a solve. Pure data: the application service maps it onto persisted
/// domain entities. Producing this object performs no I/O, so a deterministic test double
/// can return a hand-built <see cref="SolveResult"/> for the same <see cref="SolveInput"/>.
/// </summary>
public sealed record SolveResult
{
    public required string InputVersion { get; init; }

    public required IReadOnlyList<AssignmentPlan> Assignments { get; init; }

    public required IReadOnlyList<UnassignedPlan> Unassigned { get; init; }

    public required IReadOnlyList<AuditRecord> Audit { get; init; }

    /// <summary>Sum of arrival minutes across all assignments.</summary>
    public int TotalCostMinutes => Assignments.Sum(a => a.ArrivalMinutes);

    public bool HasUnassignedTasks => Unassigned.Count > 0;
}

public sealed record AssignmentPlan
{
    public required Guid TaskId { get; init; }
    public required string TaskCode { get; init; }
    public required Guid TeamId { get; init; }
    public required string TeamCode { get; init; }
    public required Guid VehicleId { get; init; }
    public required string VehicleCode { get; init; }
    public required Guid RoadSegmentId { get; init; }
    public required string RoadSegmentCode { get; init; }
    public required int ArrivalMinutes { get; init; }
}

public sealed record UnassignedPlan
{
    public required Guid TaskId { get; init; }
    public required string TaskCode { get; init; }
    public required string Code { get; init; }
    public required string Detail { get; init; }
}

public sealed record AuditRecord
{
    public required int Sequence { get; init; }
    public Guid? TaskId { get; init; }
    public required string TaskCode { get; init; }
    public required string RuleCode { get; init; }
    public required string Message { get; init; }
}
