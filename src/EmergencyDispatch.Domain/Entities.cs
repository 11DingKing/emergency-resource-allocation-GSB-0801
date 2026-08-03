namespace EmergencyDispatch.Domain;

/// <summary>A response team with a stable set of capabilities.</summary>
public class Team
{
    public Guid Id { get; set; }

    /// <summary>Stable short code, e.g. "A", "B", "C". Used for deterministic tie-breaks.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Stable capability strings this team can perform.</summary>
    public HashSet<string> Capabilities { get; set; } = new(StringComparer.Ordinal);

    public bool HasAllCapabilities(IEnumerable<string> required) =>
        required.All(Capabilities.Contains);
}

/// <summary>A vehicle in the shared pool, distinguished by its physical height.</summary>
public class Vehicle
{
    public Guid Id { get; set; }

    /// <summary>Stable short code, used for deterministic tie-breaks.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Vehicle height in metres; must not exceed a road segment's height limit.</summary>
    public decimal HeightMeters { get; set; }
}

/// <summary>
/// A mission that must be served. Durations are always minutes. Capabilities are a
/// stable string set. <see cref="DangerLevel"/> encodes life-safety severity; a rise in
/// this value is the only trigger that may justify preempting an in-progress task.
/// </summary>
public class MissionTask
{
    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Capabilities every serving team must possess.</summary>
    public HashSet<string> RequiredCapabilities { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Latest acceptable arrival time, in minutes from now.</summary>
    public int DeadlineMinutes { get; set; }

    /// <summary>Estimated on-site service time, in minutes.</summary>
    public int ServiceMinutes { get; set; }

    /// <summary>Life-safety danger level. Higher is more urgent.</summary>
    public int DangerLevel { get; set; }

    public TaskStatus Status { get; set; } = TaskStatus.Pending;

    /// <summary>Team currently executing this task (only meaningful when InProgress).</summary>
    public Guid? ExecutingTeamId { get; set; }

    /// <summary>Vehicle currently used by the executing team (only meaningful when InProgress).</summary>
    public Guid? ExecutingVehicleId { get; set; }
}

/// <summary>
/// A road segment leading to tasks. It has a height limit (metres) and an open/closed
/// flag. Closing a segment is the "road cut" event that forces rerouting.
/// </summary>
public class RoadSegment
{
    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Maximum vehicle height permitted on this segment, in metres.</summary>
    public decimal HeightLimitMeters { get; set; }

    /// <summary>False once the segment is cut. A closed segment cannot carry any vehicle.</summary>
    public bool IsOpen { get; set; } = true;
}

/// <summary>
/// An immutable, append-only record of a road status change (a "road cut" or reopen). Keyed
/// by a stable client-supplied <see cref="EventId"/> so recording the same event twice is
/// idempotent. Explanations and diffs reference the event id that last changed a road so an
/// operator can trace why a route became unavailable.
/// </summary>
public class RoadEvent
{
    public Guid Id { get; set; }

    /// <summary>Stable, client-supplied identifier, e.g. "road-r2-closed-01". Unique.</summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>Stable code of the affected road segment.</summary>
    public string RoadCode { get; set; } = string.Empty;

    /// <summary>True if the event closed the road; false if it reopened it.</summary>
    public bool Closed { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>
/// A candidate way to reach a task over a specific road segment, with a travel time in
/// minutes. A route is usable only if its road is open and the vehicle fits the height limit.
/// </summary>
public class Route
{
    public Guid Id { get; set; }

    public Guid TaskId { get; set; }

    public Guid RoadSegmentId { get; set; }

    /// <summary>Travel time over this route, in minutes.</summary>
    public int TravelMinutes { get; set; }
}
