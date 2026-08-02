using System.ComponentModel.DataAnnotations;

namespace EmergencyAllocation.Domain;

public class AllocationVersion
{
    public Guid Id { get; set; }

    [Required, MaxLength(64)]
    public string InputVersion { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string Operation { get; set; } = string.Empty;

    [MaxLength(128)]
    public string IdempotencyKey { get; set; } = string.Empty;

    public AllocationVersionStatus Status { get; set; } = AllocationVersionStatus.Pending;

    public long Sequence { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CommittedAt { get; set; }

    public long? RoadSnapshotVersion { get; set; }

    [MaxLength(1024)]
    public string? FailureReason { get; set; }

    public int TotalCostMinutes { get; set; }
    public int AssignedCount { get; set; }
    public int UnassignedCount { get; set; }

    public List<Assignment> Assignments { get; set; } = new();
    public List<AllocationAuditEntry> AuditEntries { get; set; } = new();
}
