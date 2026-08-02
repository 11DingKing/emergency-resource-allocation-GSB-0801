using System.ComponentModel.DataAnnotations;

namespace EmergencyAllocation.Domain;

public enum RoadEventKind
{
    Interruption = 0,
    Reopen = 1
}

public class RoadEvent
{
    public Guid Id { get; set; }

    [Required, MaxLength(64)]
    public string EventId { get; set; } = string.Empty;

    [Required, MaxLength(32)]
    public string RoadCode { get; set; } = string.Empty;

    public RoadEventKind Kind { get; set; }

    [MaxLength(256)]
    public string? Reason { get; set; }

    public long RoadSnapshotVersionBefore { get; set; }
    public long RoadSnapshotVersionAfter { get; set; }

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    [MaxLength(128)]
    public string? RecordedBy { get; set; }
}
