namespace EmergencyDispatch.Infrastructure;

using EmergencyDispatch.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Deterministic seed data for the scenario: three teams (A: water-rescue + first-aid,
/// B: slope-inspection + first-aid, C: all three), three vehicles at 3.4 / 2.6 / 3.0 m,
/// a life-safety task <c>T1</c> due within 35 minutes, a 60-minute slope task <c>T2</c>
/// already executed by team B, and two roads: <c>R2</c> (fast, limit 3.2 m — the road T1
/// depends on) and <c>R1</c> (high-clearance alternate, limit 4.0 m). Ids are fixed so tests
/// and explanations are reproducible.
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

    public static readonly Guid Task1 = Guid.Parse("33333333-0000-0000-0000-000000000001"); // T1 life-safety
    public static readonly Guid Task2 = Guid.Parse("33333333-0000-0000-0000-000000000002"); // T2 slope, in progress

    public static readonly Guid Road1 = Guid.Parse("44444444-0000-0000-0000-000000000001"); // R1 high-clearance, 4.0m
    public static readonly Guid Road2 = Guid.Parse("44444444-0000-0000-0000-000000000002"); // R2 fast, 3.2m

    public static readonly Guid RouteT1ViaR2 = Guid.Parse("55555555-0000-0000-0000-000000000001");
    public static readonly Guid RouteT1ViaR1 = Guid.Parse("55555555-0000-0000-0000-000000000002");
    public static readonly Guid RouteT2ViaR1 = Guid.Parse("55555555-0000-0000-0000-000000000003");

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
        new RoadSegment { Id = Road1, Code = "R1", Name = "High-clearance alternate", HeightLimitMeters = 4.0m, IsOpen = true },
        new RoadSegment { Id = Road2, Code = "R2", Name = "Fast low-clearance road", HeightLimitMeters = 3.2m, IsOpen = true },
    };

    public static IReadOnlyList<MissionTask> Tasks() => new[]
    {
        new MissionTask
        {
            Id = Task1, Code = "T1", Name = "Life-safety water rescue",
            RequiredCapabilities = new HashSet<string>(StringComparer.Ordinal)
                { Capabilities.WaterRescue, Capabilities.FirstAid },
            DeadlineMinutes = 35, ServiceMinutes = 40, DangerLevel = DangerLevels.Rank(DangerLevels.High),
            Status = TaskStatus.Pending,
        },
        new MissionTask
        {
            Id = Task2, Code = "T2", Name = "Slope inspection (in progress)",
            RequiredCapabilities = new HashSet<string>(StringComparer.Ordinal)
                { Capabilities.SlopeInspection },
            DeadlineMinutes = 60, ServiceMinutes = 60, DangerLevel = DangerLevels.Rank(DangerLevels.Routine),
            Status = TaskStatus.InProgress,
            ExecutingTeamId = TeamB, ExecutingVehicleId = VehicleLow,
        },
    };

    public static IReadOnlyList<Route> Routes() => new[]
    {
        // T1 reachable via both roads; the fast R2 (20 min) beats the R1 alternate (30 min).
        new Route { Id = RouteT1ViaR2, TaskId = Task1, RoadSegmentId = Road2, TravelMinutes = 20 },
        new Route { Id = RouteT1ViaR1, TaskId = Task1, RoadSegmentId = Road1, TravelMinutes = 30 },
        // T2 reachable via the high-clearance R1 (already being served on-site).
        new Route { Id = RouteT2ViaR1, TaskId = Task2, RoadSegmentId = Road1, TravelMinutes = 25 },
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
