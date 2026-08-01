using EmergencyAllocation.Core.Entities;
using EmergencyAllocation.Infrastructure.Services;
using EmergencyAllocation.Infrastructure.Services.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace EmergencyAllocation.Api.Controllers;

[ApiController]
[Route("api/tasks")]
public class TasksController : ControllerBase
{
    private readonly IAdministrativeDataService _dataService;

    public TasksController(IAdministrativeDataService dataService)
    {
        _dataService = dataService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<EmergencyTask>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        return Ok(await _dataService.ListTasksAsync(cancellationToken));
    }

    [HttpPatch("{taskId}")]
    public async Task<IActionResult> Update(string taskId, [FromBody] TaskStateUpdateRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _dataService.UpdateTaskAsync(taskId, request, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
