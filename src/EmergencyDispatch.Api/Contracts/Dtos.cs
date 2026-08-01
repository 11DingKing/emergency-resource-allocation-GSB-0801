namespace EmergencyDispatch.Api.Contracts;

using EmergencyDispatch.Domain;
using EmergencyDispatch.Infrastructure;

/// <summary>Request body for an initial solve or a replan, bound to a snapshot digest.</summary>
public sealed record SolveRequestDto
{
    /// <summary>Idempotency key describing the request. Required.</summary>
    public string InputVersion { get; init; } = string.Empty;

    /// <summary>
    /// Digest of the world snapshot the caller solved against (from GET /api/snapshot).
    /// Required: binds this request to a real snapshot version so replays and conflicts differ.
    /// </summary>
    public string SnapshotVersion { get; init; } = string.Empty;
}

/// <summary>The full, externally-visible representation of one allocation version.</summary>
public sealed record AllocationVersionDto
{
    public required int VersionNumber { get; init; }
    public required string InputVersion { get; init; }
    public required string SnapshotVersion { get; init; }
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
        SnapshotVersion = v.SnapshotVersion,
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
    public required string SnapshotVersion { get; init; }
    public required string Kind { get; init; }
    public required int TotalCostMinutes { get; init; }
    public required IReadOnlyList<AuditEntryDto> Audit { get; init; }
    public required IReadOnlyList<AssignmentDto> Assignments { get; init; }
    public required IReadOnlyList<UnassignedReasonDto> Unassigned { get; init; }
    public required IReadOnlyList<RoadEventDto> RoadEvents { get; init; }

    public static ExplanationDto From(AllocationExplanation e) => new()
    {
        VersionNumber = e.VersionNumber,
        InputVersion = e.InputVersion,
        SnapshotVersion = e.SnapshotVersion,
        Kind = e.Kind.ToString(),
        TotalCostMinutes = e.TotalCostMinutes,
        Audit = e.Audit.Select(AuditEntryDto.From).ToList(),
        Assignments = e.Assignments.Select(AssignmentDto.From).ToList(),
        Unassigned = e.Unassigned.Select(UnassignedReasonDto.From).ToList(),
        RoadEvents = e.RoadEvents.Select(RoadEventDto.From).ToList(),
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

/// <summary>Request body for recording a road event (a cut or reopen).</summary>
public sealed record RoadEventRequestDto
{
    /// <summary>Stable event identifier, e.g. "road-r2-closed-01". Required, idempotent.</summary>
    public string EventId { get; init; } = string.Empty;

    /// <summary>Stable code of the affected road, e.g. "R2". Required.</summary>
    public string RoadCode { get; init; } = string.Empty;

    /// <summary>True to close the road, false to reopen it.</summary>
    public bool Closed { get; init; } = true;
}

public sealed record RoadEventDto
{
    public required string EventId { get; init; }
    public required string RoadCode { get; init; }
    public required bool Closed { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }

    public static RoadEventDto From(RoadEventRef e) => new()
    {
        EventId = e.EventId,
        RoadCode = e.RoadCode,
        Closed = e.Closed,
        RecordedAt = e.RecordedAt,
    };
}

/// <summary>Result of recording a road event, including the resulting snapshot digest.</summary>
public sealed record RoadEventResultDto
{
    public required string EventId { get; init; }
    public required string RoadCode { get; init; }
    public required bool Closed { get; init; }
    public required bool WasExisting { get; init; }
    public required string SnapshotVersion { get; init; }

    public static RoadEventResultDto From(RoadEventResult r) => new()
    {
        EventId = r.EventId,
        RoadCode = r.RoadCode,
        Closed = r.Closed,
        WasExisting = r.WasExisting,
        SnapshotVersion = r.SnapshotVersion,
    };
}

/// <summary>Request body for updating a task's danger level.</summary>
public sealed record TaskDangerDto
{
    /// <summary>Stable danger-level name: routine | elevated | high | critical.</summary>
    public string DangerLevel { get; init; } = string.Empty;
}

/// <summary>The current world snapshot and its deterministic digest.</summary>
public sealed record SnapshotDto
{
    public required string SnapshotVersion { get; init; }
    public required WorldSnapshot World { get; init; }

    public static SnapshotDto From(WorldSnapshot w) => new()
    {
        SnapshotVersion = w.Version,
        World = w,
    };
}

/// <summary>
/// The 409 body returned when a repeated input version is submitted against a different
/// snapshot. Carries the field-level diff plus the road events, referencing affected entities.
/// </summary>
public sealed record ConflictDto
{
    public required string Error { get; init; }
    public required string InputVersion { get; init; }
    public required string RequestedSnapshotVersion { get; init; }
    public required string StoredSnapshotVersion { get; init; }
    public required IReadOnlyList<string> AffectedEntities { get; init; }
    public required IReadOnlyList<FieldChangeDto> Changes { get; init; }
    public required IReadOnlyList<RoadEventDto> RoadEvents { get; init; }
    public required string Message { get; init; }

    public static ConflictDto From(SnapshotConflict c) => new()
    {
        Error = "snapshot_conflict",
        InputVersion = c.InputVersion,
        RequestedSnapshotVersion = c.RequestedSnapshotVersion,
        StoredSnapshotVersion = c.StoredSnapshotVersion,
        AffectedEntities = c.Diff.AffectedEntities,
        Changes = c.Diff.Changes.Select(FieldChangeDto.From).ToList(),
        RoadEvents = c.RoadEvents.Select(RoadEventDto.From).ToList(),
        Message = c.Message,
    };
}

public sealed record FieldChangeDto
{
    public required string Path { get; init; }
    public required string Stored { get; init; }
    public required string Current { get; init; }

    public static FieldChangeDto From(FieldChange f) => new()
    {
        Path = f.Path,
        Stored = f.Stored,
        Current = f.Current,
    };
}
