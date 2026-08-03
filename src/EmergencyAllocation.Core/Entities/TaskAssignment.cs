namespace EmergencyAllocation.Core.Entities;

public class TaskAssignment
{
    public Guid Id { get; set; }
    public Guid AllocationVersionId { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public string TeamId { get; set; } = string.Empty;
    public string VehicleId { get; set; } = string.Empty;
    public List<string> RouteNodes { get; set; } = new();
    public int EstimatedArrivalMinutes { get; set; }
    public int EstimatedCompletionMinutes { get; set; }
    public AssignmentKind Kind { get; set; }
    public string? PreemptionReason { get; set; }
    public int OrderIndex { get; set; }

    public AllocationVersion? AllocationVersion { get; set; }
}
