using System.ComponentModel.DataAnnotations;

namespace EmergencyAllocation.Domain;

public class Vehicle
{
    public Guid Id { get; set; }

    [Required, MaxLength(32)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    public double HeightMeters { get; set; }
    public double AverageSpeedMetersPerMinute { get; set; } = 500d;
    public VehicleKind Kind { get; set; } = VehicleKind.Standard;
    public bool IsAvailable { get; set; } = true;

    public Guid TeamId { get; set; }
    public Team? Team { get; set; }
}
