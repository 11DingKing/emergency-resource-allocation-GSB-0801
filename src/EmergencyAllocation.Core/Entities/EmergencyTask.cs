namespace EmergencyAllocation.Core.Entities;

public class EmergencyTask
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string LocationNode { get; set; } = string.Empty;
    public int RequiredArrivalMinutes { get; set; }
    public int DurationMinutes { get; set; }
    public DangerLevel DangerLevel { get; set; }
    public TaskStatus Status { get; set; }
    public string? AssignedTeamId { get; set; }
    public List<string> RequiredCapabilities { get; set; } = new();
}
