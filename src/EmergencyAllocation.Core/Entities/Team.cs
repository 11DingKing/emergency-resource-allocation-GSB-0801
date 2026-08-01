namespace EmergencyAllocation.Core.Entities;

public class Team
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string VehicleId { get; set; } = string.Empty;
    public string HomeNode { get; set; } = string.Empty;
    public string CurrentNode { get; set; } = string.Empty;
    public bool IsAvailable { get; set; } = true;
    public List<string> Capabilities { get; set; } = new();

    public Vehicle? Vehicle { get; set; }
}
