using System.ComponentModel.DataAnnotations;

namespace EmergencyAllocation.Domain;

public class Assignment
{
    public Guid Id { get; set; }
    public Guid AllocationVersionId { get; set; }
    public AllocationVersion? AllocationVersion { get; set; }

    public Guid TaskId { get; set; }
    public EmergencyTask? Task { get; set; }

    public Guid? TeamId { get; set; }
    public Team? Team { get; set; }

    public Guid? VehicleId { get; set; }
    public Vehicle? Vehicle { get; set; }

    public AllocationDecision Decision { get; set; }

    public int EstimatedTravelMinutes { get; set; }
    public int EstimatedTotalMinutes { get; set; }

    public bool MeetsDeadline { get; set; }

    [MaxLength(1024)]
    public string? RouteNodeIds { get; set; }

    [MaxLength(2048)]
    public string? Reason { get; set; }

    public bool Preempted { get; set; }

    public Guid? PreviousTeamId { get; set; }
    public Guid? PreviousVehicleId { get; set; }
}
