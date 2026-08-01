namespace EmergencyDispatch.Api.Contracts;

using EmergencyDispatch.Domain;
using EmergencyDispatch.Infrastructure;

/// <summary>Request body for an initial solve or a replan.</summary>
public sealed record SolveRequestDto
{
    /// <summary>Idempotency key describing the input snapshot. Required.</summary>
    public string InputVersion { get; init; } = string.Empty;
}

/// <summary>The full, externally-visible representation of one allocation version.</summary>
public sealed record AllocationVersionDto
{
    public required int VersionNumber { get; init; }
    public required string InputVersion { get; init; }
    public required string Kind { get; init; }
    public required int TotalCostMinutes { get; init; }
    public required bool HasUnassignedTasks { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required IReadOnlyList<AssignmentDto> Assignments { get; init; }
    public required IReadOnlyList<UnassignedReasonDto> Unassigned { get; init; }

    public static AllocationVersionDto From(AllocationVersion v) => new()
    {
        VersionNumber = v.VersionNumber,
        InputVersion = v.InputVersion,
        Kind = v.Kind.ToString(),
        TotalCostMinutes = v.TotalCostMinutes,
        HasUnassignedTasks = v.HasUnassignedTasks,
        CreatedAt = v.CreatedAt,
        Assignments = v.Assignments
            .OrderBy(a => a.TaskCode, StringComparer.Ordinal)
            .Select(AssignmentDto.From).ToList(),
        Unassigned = v.UnassignedReasons
            .OrderBy(u => u.TaskCode, StringComparer.Ordinal)
            .Select(UnassignedReasonDto.From).ToList(),
    };
}

public sealed record AssignmentDto
{
    public required string TaskCode { get; init; }
    public required string TeamCode { get; init; }
    public required string VehicleCode { get; init; }
    public required string RoadSegmentCode { get; init; }
    public required int ArrivalMinutes { get; init; }

    public static AssignmentDto From(Assignment a) => new()
    {
        TaskCode = a.TaskCode,
        TeamCode = a.TeamCode,
        VehicleCode = a.VehicleCode,
        RoadSegmentCode = a.RoadSegmentCode,
        ArrivalMinutes = a.ArrivalMinutes,
    };
}

public sealed record UnassignedReasonDto
{
    public required string TaskCode { get; init; }
    public required string Code { get; init; }
    public required string Detail { get; init; }

    public static UnassignedReasonDto From(UnassignedReason u) => new()
    {
        TaskCode = u.TaskCode,
        Code = u.Code,
        Detail = u.Detail,
    };
}

public sealed record ExplanationDto
{
    public required int VersionNumber { get; init; }
    public required string InputVersion { get; init; }
    public required string Kind { get; init; }
    public required int TotalCostMinutes { get; init; }
    public required IReadOnlyList<AuditEntryDto> Audit { get; init; }
    public required IReadOnlyList<AssignmentDto> Assignments { get; init; }
    public required IReadOnlyList<UnassignedReasonDto> Unassigned { get; init; }

    public static ExplanationDto From(AllocationExplanation e) => new()
    {
        VersionNumber = e.VersionNumber,
        InputVersion = e.InputVersion,
        Kind = e.Kind.ToString(),
        TotalCostMinutes = e.TotalCostMinutes,
        Audit = e.Audit.Select(AuditEntryDto.From).ToList(),
        Assignments = e.Assignments.Select(AssignmentDto.From).ToList(),
        Unassigned = e.Unassigned.Select(UnassignedReasonDto.From).ToList(),
    };
}

public sealed record AuditEntryDto
{
    public required int Sequence { get; init; }
    public required string TaskCode { get; init; }
    public required string RuleCode { get; init; }
    public required string Message { get; init; }

    public static AuditEntryDto From(AuditEntry a) => new()
    {
        Sequence = a.Sequence,
        TaskCode = a.TaskCode,
        RuleCode = a.RuleCode,
        Message = a.Message,
    };
}

/// <summary>Request body for cutting or reopening a road segment.</summary>
public sealed record RoadStateDto
{
    public bool IsOpen { get; init; }
}
