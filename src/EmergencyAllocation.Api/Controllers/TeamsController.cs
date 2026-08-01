using EmergencyAllocation.Core.Entities;
using EmergencyAllocation.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace EmergencyAllocation.Api.Controllers;

[ApiController]
[Route("api/teams")]
public class TeamsController : ControllerBase
{
    private readonly IAdministrativeDataService _dataService;

    public TeamsController(IAdministrativeDataService dataService)
    {
        _dataService = dataService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<Team>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        return Ok(await _dataService.ListTeamsAsync(cancellationToken));
    }

    [HttpPatch("{teamId}/position")]
    public async Task<IActionResult> UpdatePosition(string teamId, [FromBody] UpdatePositionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _dataService.UpdateTeamPositionAsync(teamId, request.CurrentNode, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}

public sealed record UpdatePositionRequest(string CurrentNode);
