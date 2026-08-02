using System.ComponentModel.DataAnnotations;

namespace EmergencyAllocation.Domain;

public class Team
{
    public Guid Id { get; set; }

    [Required, MaxLength(32)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(64)]
    public string BaseNodeId { get; set; } = string.Empty;

    public bool IsAvailable { get; set; } = true;

    public List<TeamCapability> Capabilities { get; set; } = new();
    public List<Vehicle> Vehicles { get; set; } = new();
}

public class TeamCapability
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Team? Team { get; set; }

    [Required, MaxLength(64)]
    public string Capability { get; set; } = string.Empty;
}
