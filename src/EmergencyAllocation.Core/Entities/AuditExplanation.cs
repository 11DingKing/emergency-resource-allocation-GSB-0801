namespace EmergencyAllocation.Core.Entities;

public class AuditExplanation
{
    public Guid Id { get; set; }
    public Guid AllocationVersionId { get; set; }
    public ExplanationKind Kind { get; set; }
    public string RuleCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? RelatedTaskId { get; set; }
    public string? RelatedTeamId { get; set; }

    public AllocationVersion? AllocationVersion { get; set; }
}
