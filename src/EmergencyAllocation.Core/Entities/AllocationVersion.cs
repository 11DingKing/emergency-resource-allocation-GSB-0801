namespace EmergencyAllocation.Core.Entities;

public class AllocationVersion
{
    public Guid Id { get; set; }
    public string InputVersion { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public AllocationStatus Status { get; set; }
    public bool DangerLevelRaised { get; set; }
    public DateTimeOffset SnapshotTakenAt { get; set; }
    public string SnapshotHash { get; set; } = string.Empty;
    public string SolverVersion { get; set; } = string.Empty;
    public long TotalCost { get; set; }
    public bool IsFeasible { get; set; }
    public string? NoFeasibleReason { get; set; }
    public Guid? PreviousVersionId { get; set; }

    public List<TaskAssignment> Assignments { get; set; } = new();
    public List<AuditExplanation> Explanations { get; set; } = new();
}
