using EmergencyAllocation.Core;
using EmergencyAllocation.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TaskStatus = EmergencyAllocation.Core.TaskStatus;

namespace EmergencyAllocation.Infrastructure.Persistence;

public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider serviceProvider, ILogger? logger = null)
    {
        using var scope = serviceProvider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AllocationDbContext>>();
        await using var context = await factory.CreateDbContextAsync();

        try
        {
            await context.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Database migration skipped (provider may not support migrations).");
        }

        if (await context.Teams.AnyAsync() || await context.Tasks.AnyAsync())
        {
            logger?.LogInformation("Seed data already present; skipping.");
            return;
        }

        var vehicles = new List<Vehicle>
        {
            new() { Id = "V_A", Name = "A车-涉水救援车", HeightMeters = 3.4m },
            new() { Id = "V_B", Name = "B车-边坡巡查车", HeightMeters = 2.6m },
            new() { Id = "V_C", Name = "C车-综合救援车", HeightMeters = 3.0m }
        };

        var teams = new List<Team>
        {
            new()
            {
                Id = "A", Name = "队伍A", VehicleId = "V_A", HomeNode = "DEPOT", CurrentNode = "DEPOT",
                IsAvailable = true, Capabilities = new List<string> { Capabilities.WaterRescue, Capabilities.FirstAid }
            },
            new()
            {
                Id = "B", Name = "队伍B", VehicleId = "V_B", HomeNode = "DEPOT", CurrentNode = "SLOPE",
                IsAvailable = true, Capabilities = new List<string> { Capabilities.SlopeInspection, Capabilities.FirstAid }
            },
            new()
            {
                Id = "C", Name = "队伍C", VehicleId = "V_C", HomeNode = "DEPOT", CurrentNode = "DEPOT",
                IsAvailable = true,
                Capabilities = new List<string>
                    { Capabilities.WaterRescue, Capabilities.SlopeInspection, Capabilities.FirstAid }
            }
        };

        var tasks = new List<EmergencyTask>
        {
            new()
            {
                Id = "T1", Name = "水域被困人员救援", LocationNode = "WATER",
                RequiredArrivalMinutes = 35, DurationMinutes = 45,
                DangerLevel = DangerLevel.LifeSafety, Status = TaskStatus.Pending, AssignedTeamId = null,
                RequiredCapabilities = new List<string> { Capabilities.WaterRescue, Capabilities.FirstAid }
            },
            new()
            {
                Id = "T2", Name = "边坡加固巡查", LocationNode = "SLOPE",
                RequiredArrivalMinutes = 60, DurationMinutes = 60,
                DangerLevel = DangerLevel.Elevated, Status = TaskStatus.InProgress, AssignedTeamId = "B",
                RequiredCapabilities = new List<string> { Capabilities.SlopeInspection, Capabilities.FirstAid }
            }
        };

        var roads = new List<RoadSegment>
        {
            new()
            {
                Id = "R1", Name = "DEPOT-WATER主干道", FromNode = "DEPOT", ToNode = "WATER",
                HeightLimitMeters = 3.2m, TravelTimeMinutes = 20, IsOpen = true
            },
            new()
            {
                Id = "R2", Name = "DEPOT-SLOPE通道", FromNode = "DEPOT", ToNode = "SLOPE",
                HeightLimitMeters = 4.0m, TravelTimeMinutes = 25, IsOpen = true
            },
            new()
            {
                Id = "R3", Name = "SLOPE-WATER联络线", FromNode = "SLOPE", ToNode = "WATER",
                HeightLimitMeters = 4.0m, TravelTimeMinutes = 10, IsOpen = true
            }
        };

        context.Vehicles.AddRange(vehicles);
        context.Teams.AddRange(teams);
        context.Tasks.AddRange(tasks);
        context.RoadSegments.AddRange(roads);
        await context.SaveChangesAsync();
        logger?.LogInformation("Seed data inserted: 3 vehicles, 3 teams, 2 tasks, 3 road segments.");
    }
}
