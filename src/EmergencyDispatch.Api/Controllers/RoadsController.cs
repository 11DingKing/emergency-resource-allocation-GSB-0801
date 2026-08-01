namespace EmergencyDispatch.Api.Controllers;

using EmergencyDispatch.Api.Contracts;
using EmergencyDispatch.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Manages road segment state. Cutting a road (setting IsOpen = false) is the disruption
/// that a subsequent replan must route around. This controller performs a simple persisted
/// state change; it holds no scheduling logic.
/// </summary>
[ApiController]
[Route("api/roads")]
public sealed class RoadsController : ControllerBase
{
    private readonly DispatchDbContext _db;

    public RoadsController(DispatchDbContext db) => _db = db;

    /// <summary>List all road segments and their current open/closed state.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var roads = await _db.RoadSegments
            .AsNoTracking()
            .OrderBy(r => r.Code)
            .Select(r => new
            {
                r.Code,
                r.Name,
                r.HeightLimitMeters,
                r.IsOpen,
            })
            .ToListAsync(ct);
        return Ok(roads);
    }

    /// <summary>Cut or reopen a road segment by its stable code.</summary>
    [HttpPut("{code}/state")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetState(string code, [FromBody] RoadStateDto body, CancellationToken ct)
    {
        var road = await _db.RoadSegments.FirstOrDefaultAsync(r => r.Code == code, ct);
        if (road is null) return NotFound();

        road.IsOpen = body.IsOpen;
        await _db.SaveChangesAsync(ct);

        return Ok(new { road.Code, road.IsOpen });
    }
}
