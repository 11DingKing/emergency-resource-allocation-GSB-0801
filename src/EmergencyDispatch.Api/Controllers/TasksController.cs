namespace EmergencyDispatch.Api.Controllers;

using EmergencyDispatch.Api.Contracts;
using EmergencyDispatch.Infrastructure;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Task administration endpoints. Currently exposes danger-level changes, which are the
/// trigger a subsequent replan evaluates against the round-1 preemption conditions. No
/// scheduling logic lives here.
/// </summary>
[ApiController]
[Route("api/tasks")]
public sealed class TasksController : ControllerBase
{
    private readonly IAllocationService _service;

    public TasksController(IAllocationService service) => _service = service;

    /// <summary>Set a task's danger level to a stable name (routine|elevated|high|critical).</summary>
    [HttpPut("{code}/danger")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDanger(string code, [FromBody] TaskDangerDto body, CancellationToken ct)
    {
        try
        {
            var updated = await _service.SetTaskDangerAsync(code, body.DangerLevel, ct);
            return updated ? Ok(new { code, dangerLevel = body.DangerLevel }) : NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
