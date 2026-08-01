using EmergencyDispatch.Api.Contracts;
using EmergencyDispatch.Domain;
using EmergencyDispatch.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EmergencyDispatch.Api.Controllers;

[ApiController]
[Route("api/tasks")]
public class TasksController(DispatchDbContext db) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TaskDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tasks = await db.Tasks.Include(t => t.CurrentTeam).OrderBy(t => t.Code)
            .Select(t => new TaskDto(t.Id, t.Code, t.Title, t.Kind.ToString(), t.RequiredCapabilities,
                t.DeadlineMinutes, t.DurationMinutes, t.Danger.ToString(), t.Status.ToString(),
                t.CurrentTeam != null ? t.CurrentTeam.Code : null))
            .ToListAsync(ct);
        return Ok(tasks);
    }

    /// <summary>
    /// 危险等级调整（standard/elevated/critical）。
    /// 生命安全任务升至 critical 后，replan 才允许对执行中任务做抢占式重排，且必须记录原因。
    /// </summary>
    [HttpPost("{code}/danger")]
    [ProducesResponseType(typeof(TaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDanger(string code, [FromBody] DangerUpdateDto dto, CancellationToken ct)
    {
        if (!Enum.TryParse<DangerLevel>(dto.Level, ignoreCase: true, out var level))
            return BadRequest(new ProblemDetails { Title = "请求不合法", Detail = "level 必须是 standard / elevated / critical。", Status = StatusCodes.Status400BadRequest });

        var task = await db.Tasks.Include(t => t.CurrentTeam).FirstOrDefaultAsync(t => t.Code == code, ct);
        if (task is null)
            return NotFound(new ProblemDetails { Title = "未找到", Detail = $"任务 {code} 不存在。", Status = StatusCodes.Status404NotFound });

        task.Danger = level;
        await db.SaveChangesAsync(ct);
        return Ok(new TaskDto(task.Id, task.Code, task.Title, task.Kind.ToString(), task.RequiredCapabilities,
            task.DeadlineMinutes, task.DurationMinutes, task.Danger.ToString(), task.Status.ToString(),
            task.CurrentTeam?.Code));
    }
}
