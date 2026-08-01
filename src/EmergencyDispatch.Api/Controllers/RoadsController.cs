using EmergencyDispatch.Api.Contracts;
using EmergencyDispatch.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EmergencyDispatch.Api.Controllers;

[ApiController]
[Route("api/roads")]
public class RoadsController(DispatchDbContext db) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RoadDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var roads = await db.RoadSegments.OrderBy(r => r.Code)
            .Select(r => new RoadDto(r.Id, r.Code, r.Name, r.MaxVehicleHeightMeters, r.TravelMinutes, r.IsBlocked))
            .ToListAsync(ct);
        return Ok(roads);
    }

    /// <summary>道路中断/恢复。幂等：重复设置同一状态结果一致。变更后以新的 inputVersion 发起重排。</summary>
    [HttpPatch("{code}")]
    [ProducesResponseType(typeof(RoadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(string code, [FromBody] RoadUpdateDto dto, CancellationToken ct)
    {
        var road = await db.RoadSegments.FirstOrDefaultAsync(r => r.Code == code, ct);
        if (road is null)
            return NotFound(new ProblemDetails { Title = "未找到", Detail = $"道路 {code} 不存在。", Status = StatusCodes.Status404NotFound });

        road.IsBlocked = dto.IsBlocked;
        await db.SaveChangesAsync(ct);
        return Ok(new RoadDto(road.Id, road.Code, road.Name, road.MaxVehicleHeightMeters, road.TravelMinutes, road.IsBlocked));
    }
}
