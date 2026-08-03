namespace EmergencyAllocation.Core.Entities;

public class RoadEvent
{
    public string Id { get; set; } = string.Empty;
    public string RoadSegmentId { get; set; } = string.Empty;
    public bool IsOpen { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public string? RecordedBy { get; set; }

    public RoadSegment? RoadSegment { get; set; }
}
