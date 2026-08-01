namespace EmergencyDispatch.Infrastructure;

using EmergencyDispatch.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Deterministic seed data matching the scenario: three teams (A: water-rescue + first-aid,
/// B: slope-inspection + first-aid, C: all three), three vehicles at 3.4 / 2.6 / 3.0 m,
/// one life-safety task due within 35 minutes, one 60-minute slope task already executed by
/// team B, and two roads limited to 3.2 m and 4.0 m. Ids are fixed so tests and explanations
/// are reproducible.
/// </summary>
public static class SeedData
{
    // Fixed GUIDs keep seeding idempotent and make audit output reproducible.
    public static readonly Guid TeamA = Guid.Parse("11111111-0000-0000-0000-000000000001");
    public static readonly Guid TeamB = Guid.Parse("11111111-0000-0000-0000-000000000002");
    public static readonly Guid TeamC = Guid.Parse("11111111-0000-0000-0000-000000000003");

    public static readonly Guid VehicleHigh = Guid.Parse("22222222-0000-0000-0000-000000000001"); // 3.4m
    public static readonly Guid VehicleLow = Guid.Parse("22222222-0000-0000-0000-000000000002");  // 2.6m
    public static readonly Guid VehicleMid = Guid.Parse("22222222-0000-0000-0000-000000000003");  // 3.0m

    public static readonly Guid TaskLifeSafety = Guid.Parse("33333333-0000-0000-0000-000000000001");
    public static readonly Guid TaskSlope = Guid.Parse("33333333-0000-0000-0000-000000000002");

    public static readonly Guid RoadLow = Guid.Parse("44444444-0000-0000-0000-000000000001");  // 3.2m limit
    public static readonly Guid RoadHigh = Guid.Parse("44444444-0000-0000-0000-000000000002"); // 4.0m limit

    public static readonly Guid RouteLifeLow = Guid.Parse("55555555-0000-0000-0000-000000000001");
    public static readonly Guid RouteLifeHigh = Guid.Parse("55555555-0000-0000-0000-000000000002");
    public static readonly Guid RouteSlopeHigh = Guid.Parse("55555555-0000-0000-0000-000000000003");

    public static IReadOnlyList<Team> Teams() => new[]
    {
        new Team
        {
            Id = TeamA, Code = "A", Name = "Team A",
            Capabilities = new HashSet<string>(StringComparer.Ordinal)
                { Capabilities.WaterRescue, Capabilities.FirstAid },
        },
        new Team
        {
            Id = TeamB, Code = "B", Name = "Team B",
            Capabilities = new HashSet<string>(StringComparer.Ordinal)
                { Capabilities.SlopeInspection, Capabilities.FirstAid },
        },
        new Team
        {
            Id = TeamC, Code = "C", Name = "Team C",
            Capabilities = new HashSet<string>(StringComparer.Ordinal)
                { Capabilities.WaterRescue, Capabilities.SlopeInspection, Capabilities.FirstAid },
        },
    };

    public static IReadOnlyList<Vehicle> Vehicles() => new[]
    {
        new Vehicle { Id = VehicleHigh, Code = "V-HIGH", HeightMeters = 3.4m },
        new Vehicle { Id = VehicleLow, Code = "V-LOW", HeightMeters = 2.6m },
        new Vehicle { Id = VehicleMid, Code = "V-MID", HeightMeters = 3.0m },
    };

    public static IReadOnlyList<RoadSegment> Roads() => new[]
    {
        new RoadSegment { Id = RoadLow, Code = "R-LOW", Name = "Low-clearance road", HeightLimitMeters = 3.2m, IsOpen = true },
        new RoadSegment { Id = RoadHigh, Code = "R-HIGH", Name = "High-clearance road", HeightLimitMeters = 4.0m, IsOpen = true },
    };

    public static IReadOnlyList<MissionTask> Tasks() => new[]
    {
        new MissionTask
        {
            Id = TaskLifeSafety, Code = "T-LIFE", Name = "Life-safety water rescue",
            RequiredCapabilities = new HashSet<string>(StringComparer.Ordinal)
                { Capabilities.WaterRescue, Capabilities.FirstAid },
            DeadlineMinutes = 35, ServiceMinutes = 40, DangerLevel = 3,
            Status = TaskStatus.Pending,
        },
        new MissionTask
        {
            Id = TaskSlope, Code = "T-SLOPE", Name = "Slope inspection (in progress)",
            RequiredCapabilities = new HashSet<string>(StringComparer.Ordinal)
                { Capabilities.SlopeInspection },
            DeadlineMinutes = 60, ServiceMinutes = 60, DangerLevel = 1,
            Status = TaskStatus.InProgress,
            ExecutingTeamId = TeamB, ExecutingVehicleId = VehicleLow,
        },
    };

    public static IReadOnlyList<Route> Routes() => new[]
    {
        // Life-safety task reachable via both roads; low-clearance road is faster (20 min).
        new Route { Id = RouteLifeLow, TaskId = TaskLifeSafety, RoadSegmentId = RoadLow, TravelMinutes = 20 },
        new Route { Id = RouteLifeHigh, TaskId = TaskLifeSafety, RoadSegmentId = RoadHigh, TravelMinutes = 30 },
        // Slope task reachable via the high-clearance road (already being served on-site).
        new Route { Id = RouteSlopeHigh, TaskId = TaskSlope, RoadSegmentId = RoadHigh, TravelMinutes = 25 },
    };

    /// <summary>Idempotently populate an empty database with the scenario baseline.</summary>
    public static async Task EnsureSeededAsync(DispatchDbContext db, CancellationToken ct = default)
    {
        if (await db.Teams.AnyAsync(ct)) return;

        db.Teams.AddRange(Teams());
        db.Vehicles.AddRange(Vehicles());
        db.RoadSegments.AddRange(Roads());
        db.Tasks.AddRange(Tasks());
        db.Routes.AddRange(Routes());
        await db.SaveChangesAsync(ct);
    }
}
