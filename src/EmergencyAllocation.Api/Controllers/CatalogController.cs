using EmergencyAllocation.Domain;
using EmergencyAllocation.Domain.Dtos;
using EmergencyAllocation.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EmergencyAllocation.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CatalogController : ControllerBase
{
    private readonly IDbContextFactory<AllocationDbContext> _factory;

    public CatalogController(IDbContextFactory<AllocationDbContext> factory)
    {
        _factory = factory;
    }

    [HttpGet("teams")]
    public async Task<ActionResult<IReadOnlyList<TeamDto>>> Teams(CancellationToken ct)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var teams = await ctx.Teams.AsNoTracking()
            .Include(t => t.Capabilities).Include(t => t.Vehicles)
            .OrderBy(t => t.Code).ToListAsync(ct);
        return Ok(teams.Select(t => new TeamDto(
            t.Id, t.Code, t.Name, t.BaseNodeId, t.IsAvailable,
            t.Capabilities.Select(c => c.Capability).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            t.Vehicles.OrderBy(v => v.Code).Select(v => new VehicleDto(
                v.Id, v.Code, v.Name, v.HeightMeters, v.AverageSpeedMetersPerMinute,
                v.Kind.ToString(), v.IsAvailable)).ToList())).ToList());
    }

    [HttpGet("tasks")]
    public async Task<ActionResult<IReadOnlyList<TaskDto>>> Tasks(CancellationToken ct)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var tasks = await ctx.Tasks.AsNoTracking()
            .Include(t => t.RequiredCapabilities)
            .OrderBy(t => t.Code).ToListAsync(ct);
        return Ok(tasks.Select(t => new TaskDto(
            t.Id, t.Code, t.Title, t.LocationNodeId,
            t.Severity.ToString(), t.Status.ToString(),
            t.DurationMinutes, t.DeadlineMinutes,
            t.SeverityVersion, t.AssignedTeamId, t.AssignedVehicleId,
            t.StartedAt,
            t.RequiredCapabilities.Select(c => c.Capability).OrderBy(x => x, StringComparer.Ordinal).ToList())).ToList());
    }

    [HttpGet("roads")]
    public async Task<ActionResult<IReadOnlyList<RoadSegmentDto>>> Roads(CancellationToken ct)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var roads = await ctx.RoadSegments.AsNoTracking()
            .OrderBy(r => r.Code).ToListAsync(ct);
        return Ok(roads.Select(r => new RoadSegmentDto(
            r.Id, r.Code, r.FromNodeId, r.ToNodeId,
            r.TravelTimeMinutes, r.HeightLimitMeters, r.IsOpen,
            r.RoadSnapshotVersion, r.UpdatedAt, r.InterruptionReason)).ToList());
    }
}
