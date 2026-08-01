namespace EmergencyDispatch.Solver;

/// <summary>
/// Immutable input snapshot handed to a solver. This is a pure data transfer type with no
/// dependency on Entity Framework, the database or ASP.NET. The application service builds
/// it from a consistent read; the solver treats it as the entire world it may reason about.
/// A given snapshot always maps to a single deterministic <see cref="SolveResult"/>.
/// </summary>
public sealed record SolveInput
{
    /// <summary>Idempotency key describing this input snapshot.</summary>
    public required string InputVersion { get; init; }

    /// <summary>True when this solve is a replan of an existing plan.</summary>
    public bool IsReplan { get; init; }

    public required IReadOnlyList<TeamSnapshot> Teams { get; init; }
    public required IReadOnlyList<VehicleSnapshot> Vehicles { get; init; }
    public required IReadOnlyList<TaskSnapshot> Tasks { get; init; }
    public required IReadOnlyList<RoadSnapshot> Roads { get; init; }
    public required IReadOnlyList<RouteSnapshot> Routes { get; init; }
}

public sealed record TeamSnapshot
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required IReadOnlySet<string> Capabilities { get; init; }
}

public sealed record VehicleSnapshot
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required decimal HeightMeters { get; init; }
}

public sealed record TaskSnapshot
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required IReadOnlySet<string> RequiredCapabilities { get; init; }
    public required int DeadlineMinutes { get; init; }
    public required int ServiceMinutes { get; init; }
    public required int DangerLevel { get; init; }

    /// <summary>True if the task is already being executed and therefore protected.</summary>
    public required bool IsInProgress { get; init; }

    /// <summary>Danger level recorded when the in-progress task was last planned.</summary>
    public int? PreviousDangerLevel { get; init; }

    public Guid? ExecutingTeamId { get; init; }
    public Guid? ExecutingVehicleId { get; init; }
}

public sealed record RoadSnapshot
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required decimal HeightLimitMeters { get; init; }
    public required bool IsOpen { get; init; }
}

public sealed record RouteSnapshot
{
    public required Guid Id { get; init; }
    public required Guid TaskId { get; init; }
    public required Guid RoadSegmentId { get; init; }
    public required int TravelMinutes { get; init; }
}
