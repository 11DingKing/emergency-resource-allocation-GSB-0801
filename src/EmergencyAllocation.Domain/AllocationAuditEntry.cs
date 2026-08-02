using System.ComponentModel.DataAnnotations;

namespace EmergencyAllocation.Domain;

public class AllocationAuditEntry
{
    public Guid Id { get; set; }
    public Guid AllocationVersionId { get; set; }
    public AllocationVersion? AllocationVersion { get; set; }

    public int Order { get; set; }

    [MaxLength(32)]
    public string Kind { get; set; } = string.Empty;

    [MaxLength(32)]
    public string? TaskCode { get; set; }

    [MaxLength(32)]
    public string? TeamCode { get; set; }

    [MaxLength(32)]
    public string? VehicleCode { get; set; }

    [MaxLength(32)]
    public string? RoadCode { get; set; }

    [MaxLength(64)]
    public string? RoadEventId { get; set; }

    [MaxLength(2048)]
    public string Message { get; set; } = string.Empty;
}
