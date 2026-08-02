using System.ComponentModel.DataAnnotations;

namespace EmergencyAllocation.Domain;

public class RoadSegment
{
    public Guid Id { get; set; }

    [Required, MaxLength(32)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(64)]
    public string FromNodeId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string ToNodeId { get; set; } = string.Empty;

    public int TravelTimeMinutes { get; set; }

    public double? HeightLimitMeters { get; set; }

    public bool IsOpen { get; set; } = true;

    public long RoadSnapshotVersion { get; set; } = 1;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [MaxLength(256)]
    public string? InterruptionReason { get; set; }
}
