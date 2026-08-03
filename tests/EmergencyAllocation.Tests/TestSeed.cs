using EmergencyAllocation.Core;
using EmergencyAllocation.Core.Entities;
using EmergencyAllocation.Core.Solving;
using EmergencyAllocation.Infrastructure.Persistence;
using TaskStatus = EmergencyAllocation.Core.TaskStatus;

namespace EmergencyAllocation.Tests;

public static class TestSeed
{
    public const string Depot = "DEPOT";
    public const string Water = "WATER";
    public const string Slope = "SLOPE";

    public static List<Vehicle> Vehicles => new()
    {
        new Vehicle { Id = "V_A", Name = "A车", HeightMeters = 3.4m },
        new Vehicle { Id = "V_B", Name = "B车", HeightMeters = 2.6m },
        new Vehicle { Id = "V_C", Name = "C车", HeightMeters = 3.0m }
    };

    public static List<Team> Teams(string bCurrentNode = Slope, string cCurrentNode = Depot) => new()
    {
        new Team
        {
            Id = "A", Name = "队伍A", VehicleId = "V_A", HomeNode = Depot, CurrentNode = Depot,
            IsAvailable = true, Capabilities = new List<string> { Capabilities.WaterRescue, Capabilities.FirstAid }
        },
        new Team
        {
            Id = "B", Name = "队伍B", VehicleId = "V_B", HomeNode = Depot, CurrentNode = bCurrentNode,
            IsAvailable = true, Capabilities = new List<string> { Capabilities.SlopeInspection, Capabilities.FirstAid }
        },
        new Team
        {
            Id = "C", Name = "队伍C", VehicleId = "V_C", HomeNode = Depot, CurrentNode = cCurrentNode,
            IsAvailable = true,
            Capabilities = new List<string>
                { Capabilities.WaterRescue, Capabilities.SlopeInspection, Capabilities.FirstAid }
        }
    };

    public static List<EmergencyTask> Tasks(
        TaskStatus t1Status = TaskStatus.Pending,
        string? t1AssignedTeam = null,
        TaskStatus t2Status = TaskStatus.InProgress,
        string? t2AssignedTeam = "B") => new()
    {
        new EmergencyTask
        {
            Id = "T1", Name = "水域被困人员救援", LocationNode = Water,
            RequiredArrivalMinutes = 35, DurationMinutes = 45,
            DangerLevel = DangerLevel.LifeSafety, Status = t1Status, AssignedTeamId = t1AssignedTeam,
            RequiredCapabilities = new List<string> { Capabilities.WaterRescue, Capabilities.FirstAid }
        },
        new EmergencyTask
        {
            Id = "T2", Name = "边坡加固巡查", LocationNode = Slope,
            RequiredArrivalMinutes = 60, DurationMinutes = 60,
            DangerLevel = DangerLevel.Elevated, Status = t2Status, AssignedTeamId = t2AssignedTeam,
            RequiredCapabilities = new List<string> { Capabilities.SlopeInspection, Capabilities.FirstAid }
        }
    };

    public static List<RoadSegment> Roads(
        bool r1Open = true,
        bool r2Open = true,
        bool r3Open = true) => new()
    {
        new RoadSegment
        {
            Id = "R1", Name = "DEPOT-WATER", FromNode = Depot, ToNode = Water,
            HeightLimitMeters = 3.2m, TravelTimeMinutes = 20, IsOpen = r1Open
        },
        new RoadSegment
        {
            Id = "R2", Name = "DEPOT-SLOPE", FromNode = Depot, ToNode = Slope,
            HeightLimitMeters = 4.0m, TravelTimeMinutes = 25, IsOpen = r2Open
        },
        new RoadSegment
        {
            Id = "R3", Name = "SLOPE-WATER", FromNode = Slope, ToNode = Water,
            HeightLimitMeters = 4.0m, TravelTimeMinutes = 10, IsOpen = r3Open
        }
    };

    public static async Task SeedAsync(AllocationDbContext context)
    {
        context.Vehicles.AddRange(Vehicles);
        context.Teams.AddRange(Teams());
        context.Tasks.AddRange(Tasks());
        context.RoadSegments.AddRange(Roads());
        await context.SaveChangesAsync();
    }
}
