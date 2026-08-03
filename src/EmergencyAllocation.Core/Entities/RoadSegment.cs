namespace EmergencyAllocation.Core.Entities;

public class RoadSegment
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FromNode { get; set; } = string.Empty;
    public string ToNode { get; set; } = string.Empty;
    public decimal HeightLimitMeters { get; set; }
    public int TravelTimeMinutes { get; set; }
    public bool IsOpen { get; set; } = true;
}
