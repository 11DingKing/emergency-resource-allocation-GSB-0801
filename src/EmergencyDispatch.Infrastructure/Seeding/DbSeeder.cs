using EmergencyDispatch.Domain;
using EmergencyDispatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmergencyDispatch.Infrastructure.Seeding;

/// <summary>
/// 种子数据：3 支队伍 / 3 辆车 / 2 条限高道路 / 1 项 35 分钟生命安全任务 / 1 项 B 在执行中的 60 分钟边坡任务。
/// 仅在空库时写入，可重复调用。
/// </summary>
public static class DbSeeder
{
    public static async Task SeedIfEmptyAsync(DispatchDbContext db, CancellationToken ct = default)
    {
        if (await db.Teams.AnyAsync(ct))
            return;

        var teamA = new Team { Id = Guid.NewGuid(), Code = "A", Name = "Alpha 水上急救队", Capabilities = new() { "water_rescue", "first_aid" } };
        var teamB = new Team { Id = Guid.NewGuid(), Code = "B", Name = "Bravo 边坡巡查队", Capabilities = new() { "first_aid", "slope_patrol" } };
        var teamC = new Team { Id = Guid.NewGuid(), Code = "C", Name = "Charlie 综合救援队", Capabilities = new() { "first_aid", "slope_patrol", "water_rescue" } };

        db.Teams.AddRange(teamA, teamB, teamC);
        db.Vehicles.AddRange(
            new Vehicle { Id = Guid.NewGuid(), Name = "车A-冲锋舟运输车", HeightMeters = 3.4m, TeamId = teamA.Id },
            new Vehicle { Id = Guid.NewGuid(), Name = "车B-轻型巡查车", HeightMeters = 2.6m, TeamId = teamB.Id },
            new Vehicle { Id = Guid.NewGuid(), Name = "车C-中型装备车", HeightMeters = 3.0m, TeamId = teamC.Id });

        db.RoadSegments.AddRange(
            new RoadSegment { Id = Guid.NewGuid(), Code = "R1", Name = "滨河公路", MaxVehicleHeightMeters = 3.2m, TravelMinutes = 34, IsBlocked = false },
            new RoadSegment { Id = Guid.NewGuid(), Code = "R2", Name = "山岭高架", MaxVehicleHeightMeters = 4.0m, TravelMinutes = 30, IsBlocked = false });

        db.Tasks.AddRange(
            new DispatchTask
            {
                Id = Guid.NewGuid(),
                Code = "T1",
                Title = "积水点被困人员救援",
                Kind = TaskKind.LifeSafety,
                RequiredCapabilities = new() { "first_aid" },
                DeadlineMinutes = 35,
                DurationMinutes = 40,
                Danger = DangerLevel.Standard,
                Status = DispatchTaskStatus.Pending
            },
            new DispatchTask
            {
                Id = Guid.NewGuid(),
                Code = "T2",
                Title = "北坡边坡加固巡查",
                Kind = TaskKind.SlopeOperation,
                RequiredCapabilities = new() { "slope_patrol" },
                DeadlineMinutes = null,
                DurationMinutes = 60,
                Danger = DangerLevel.Standard,
                Status = DispatchTaskStatus.InProgress,
                CurrentTeamId = teamB.Id
            });

        await db.SaveChangesAsync(ct);
    }
}
