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

    /// <summary>
    /// 执行状态变更。in_progress：已出发执行（锁定给当前队伍，之后重排不因 ETA 变短被移动）；
    /// completed：已完成（之后不再参与分配）。重复设置同一状态为幂等。
    /// </summary>
    [HttpPost("{code}/execution")]
    [ProducesResponseType(typeof(TaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetExecution(string code, [FromBody] TaskExecutionDto dto, CancellationToken ct)
    {
        var target = dto.Status?.ToLowerInvariant() switch
        {
            "in_progress" => DispatchTaskStatus.InProgress,
            "completed" => DispatchTaskStatus.Completed,
            _ => (DispatchTaskStatus?)null
        };
        if (target is null)
            return BadRequest(new ProblemDetails { Title = "请求不合法", Detail = "status 必须是 in_progress 或 completed。", Status = StatusCodes.Status400BadRequest });

        var task = await db.Tasks.Include(t => t.CurrentTeam).FirstOrDefaultAsync(t => t.Code == code, ct);
        if (task is null)
            return NotFound(new ProblemDetails { Title = "未找到", Detail = $"任务 {code} 不存在。", Status = StatusCodes.Status404NotFound });

        if (task.Status == DispatchTaskStatus.Completed)
            return BadRequest(new ProblemDetails { Title = "请求不合法", Detail = $"任务 {code} 已完成，不能变更执行状态。", Status = StatusCodes.Status400BadRequest });

        if (target == DispatchTaskStatus.InProgress && task.CurrentTeamId is null)
            return BadRequest(new ProblemDetails { Title = "请求不合法", Detail = $"任务 {code} 尚未分配队伍，不能标记为执行中。", Status = StatusCodes.Status400BadRequest });

        task.Status = target.Value;
        await db.SaveChangesAsync(ct);
        return Ok(new TaskDto(task.Id, task.Code, task.Title, task.Kind.ToString(), task.RequiredCapabilities,
            task.DeadlineMinutes, task.DurationMinutes, task.Danger.ToString(), task.Status.ToString(),
            task.CurrentTeam?.Code));
    }
}
