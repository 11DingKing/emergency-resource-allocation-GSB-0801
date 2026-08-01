using EmergencyDispatch.Api.Contracts;
using EmergencyDispatch.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace EmergencyDispatch.Api.Controllers;

[ApiController]
[Route("api/world")]
public class WorldController(WorldSnapshotService snapshots) : ControllerBase
{
    /// <summary>捕获当前世界快照（道路/任务/队伍/车辆内容摘要）。相同世界状态按摘要去重，返回同一快照。</summary>
    [HttpPost("snapshots")]
    [ProducesResponseType(typeof(SnapshotDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(SnapshotDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Capture(CancellationToken ct)
    {
        var snapshot = await snapshots.CaptureAsync(ct);
        var dto = PlanMapper.ToDto(snapshot);
        return CreatedAtAction(nameof(GetSnapshot), new { id = dto.Id }, dto);
    }

    [HttpGet("snapshots/{id:guid}")]
    [ProducesResponseType(typeof(SnapshotDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSnapshot(Guid id, CancellationToken ct)
    {
        var snapshot = await snapshots.LoadAsync(id, ct);
        return snapshot is null
            ? NotFound(new ProblemDetails { Title = "未找到", Detail = "快照不存在。", Status = StatusCodes.Status404NotFound })
            : Ok(PlanMapper.ToDto(snapshot));
    }
}
