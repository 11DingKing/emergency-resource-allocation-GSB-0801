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
    private readonly IAllocationService _service;

    public RoadsController(DispatchDbContext db, IAllocationService service)
    {
        _db = db;
        _service = service;
    }

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

    /// <summary>
    /// Record a road event (a cut or reopen) with a stable event id, applying it to the road
    /// and returning the resulting snapshot digest. Idempotent on <c>eventId</c>. This is the
    /// preferred way to disrupt a road because the event id is referenced by explanations,
    /// reroute audits and conflict diffs.
    /// </summary>
    [HttpPost("events")]
    [ProducesResponseType(typeof(RoadEventResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RoadEventResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RecordEvent([FromBody] RoadEventRequestDto body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.EventId) || string.IsNullOrWhiteSpace(body.RoadCode))
        {
            return BadRequest(new { error = "eventId and roadCode are required." });
        }

        try
        {
            var result = await _service.RecordRoadEventAsync(body.EventId, body.RoadCode, body.Closed, ct);
            var dto = RoadEventResultDto.From(result);
            return result.WasExisting ? Ok(dto) : StatusCode(StatusCodes.Status201Created, dto);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Cut or reopen a road segment by its stable code (simple state change, no event id).</summary>
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
