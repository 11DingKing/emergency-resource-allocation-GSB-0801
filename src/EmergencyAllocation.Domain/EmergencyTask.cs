using System.ComponentModel.DataAnnotations;

namespace EmergencyAllocation.Domain;

public class EmergencyTask
{
    public Guid Id { get; set; }

    [Required, MaxLength(32)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(256)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(64)]
    public string LocationNodeId { get; set; } = string.Empty;

    public TaskSeverity Severity { get; set; } = TaskSeverity.Routine;
    public TaskStatus Status { get; set; } = TaskStatus.Pending;

    public int DurationMinutes { get; set; }
    public int? DeadlineMinutes { get; set; }

    public int SeverityVersion { get; set; } = 1;

    public Guid? AssignedTeamId { get; set; }
    public Team? AssignedTeam { get; set; }

    public Guid? AssignedVehicleId { get; set; }
    public Vehicle? AssignedVehicle { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public List<TaskCapabilityRequirement> RequiredCapabilities { get; set; } = new();
}

public class TaskCapabilityRequirement
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public EmergencyTask? Task { get; set; }

    [Required, MaxLength(64)]
    public string Capability { get; set; } = string.Empty;
}
