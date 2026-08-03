using EmergencyDispatch.Api.Contracts;
using EmergencyDispatch.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EmergencyDispatch.Api.Controllers;

[ApiController]
[Route("api/teams")]
public class TeamsController(DispatchDbContext db) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TeamDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var teams = await db.Teams.Include(t => t.Vehicle)
            .OrderBy(t => t.Code)
            .Select(t => new TeamDto(t.Id, t.Code, t.Name, t.Capabilities, t.Vehicle!.Name, t.Vehicle!.HeightMeters))
            .ToListAsync(ct);
        return Ok(teams);
    }
}
