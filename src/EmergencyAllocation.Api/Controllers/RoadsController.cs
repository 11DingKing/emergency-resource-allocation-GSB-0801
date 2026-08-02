using EmergencyAllocation.Core.Entities;
using EmergencyAllocation.Infrastructure.Services;
using EmergencyAllocation.Infrastructure.Services.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace EmergencyAllocation.Api.Controllers;

[ApiController]
[Route("api/roads")]
public class RoadsController : ControllerBase
{
    private readonly IAdministrativeDataService _dataService;

    public RoadsController(IAdministrativeDataService dataService)
    {
        _dataService = dataService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RoadSegment>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        return Ok(await _dataService.ListRoadsAsync(cancellationToken));
    }

    [HttpGet("events")]
    [ProducesResponseType(typeof(IReadOnlyList<RoadEvent>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListEvents([FromQuery] string? roadSegmentId, CancellationToken cancellationToken)
    {
        return Ok(await _dataService.ListRoadEventsAsync(roadSegmentId, cancellationToken));
    }

    [HttpPost("events")]
    [ProducesResponseType(typeof(RoadEvent), StatusCodes.Status201Created)]
    public async Task<IActionResult> RecordEvent([FromBody] RoadEventRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.EventId))
        {
            return BadRequest("eventId is required.");
        }

        try
        {
            var evt = await _dataService.RecordRoadEventAsync(request, cancellationToken);
            return CreatedAtAction(nameof(ListEvents), new { roadSegmentId = evt.RoadSegmentId }, evt);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    [HttpPatch("{roadId}")]
    public async Task<IActionResult> Update(string roadId, [FromBody] RoadUpdateRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _dataService.UpdateRoadAsync(roadId, request, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
