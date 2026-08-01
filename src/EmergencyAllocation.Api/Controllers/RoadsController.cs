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
