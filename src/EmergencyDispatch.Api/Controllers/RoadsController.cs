using EmergencyDispatch.Api.Contracts;
using EmergencyDispatch.Domain;
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

    /// <summary>
    /// 记录道路事件并应用其状态（如 road-r2-closed-01）。
    /// EventId 幂等：同一事件重复提交返回同一记录；同 EventId 不同内容返回 409。
    /// </summary>
    [HttpPost("events")]
    [ProducesResponseType(typeof(RoadEventDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(RoadEventDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RecordEvent([FromBody] RoadEventRequestDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.EventId) || dto.EventId.Length > 128)
            return BadRequest(new ProblemDetails { Title = "请求不合法", Detail = "eventId 必填且不超过 128 字符。", Status = StatusCodes.Status400BadRequest });

        var existing = await db.RoadEvents.Include(e => e.Road).FirstOrDefaultAsync(e => e.EventId == dto.EventId, ct);
        if (existing is not null)
        {
            if (existing.Road!.Code == dto.RoadCode && existing.IsBlocked == dto.IsBlocked)
                return Ok(new RoadEventDto(existing.Id, existing.EventId, existing.Road.Code, existing.IsBlocked, existing.Note, existing.RecordedAtUtc));
            return Conflict(new ProblemDetails { Title = "事件冲突", Detail = $"事件 {dto.EventId} 已存在且内容不同。", Status = StatusCodes.Status409Conflict });
        }

        var road = await db.RoadSegments.FirstOrDefaultAsync(r => r.Code == dto.RoadCode, ct);
        if (road is null)
            return NotFound(new ProblemDetails { Title = "未找到", Detail = $"道路 {dto.RoadCode} 不存在。", Status = StatusCodes.Status404NotFound });

        road.IsBlocked = dto.IsBlocked;
        road.LastEventId = dto.EventId;
        var roadEvent = new RoadEvent
        {
            Id = Guid.NewGuid(),
            EventId = dto.EventId,
            RoadId = road.Id,
            IsBlocked = dto.IsBlocked,
            Note = dto.Note,
            RecordedAtUtc = DateTimeOffset.UtcNow
        };
        db.RoadEvents.Add(roadEvent);
        await db.SaveChangesAsync(ct);
        return Created($"/api/roads/events/{roadEvent.Id}",
            new RoadEventDto(roadEvent.Id, roadEvent.EventId, road.Code, roadEvent.IsBlocked, roadEvent.Note, roadEvent.RecordedAtUtc));
    }

    /// <summary>道路事件列表（审计）。</summary>
    [HttpGet("events")]
    [ProducesResponseType(typeof(IReadOnlyList<RoadEventDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListEvents(CancellationToken ct)
    {
        var events = await db.RoadEvents.Include(e => e.Road)
            .OrderBy(e => e.RecordedAtUtc)
            .Select(e => new RoadEventDto(e.Id, e.EventId, e.Road!.Code, e.IsBlocked, e.Note, e.RecordedAtUtc))
            .ToListAsync(ct);
        return Ok(events);
    }
}
