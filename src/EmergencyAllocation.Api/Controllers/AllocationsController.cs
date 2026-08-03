using EmergencyAllocation.Domain.Dtos;
using EmergencyAllocation.Domain.Services;
using Microsoft.AspNetCore.Mvc;

namespace EmergencyAllocation.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AllocationsController : ControllerBase
{
    private readonly IAllocationService _allocationService;

    public AllocationsController(IAllocationService allocationService)
    {
        _allocationService = allocationService;
    }

    [HttpPost("initial")]
    [ProducesResponseType(typeof(AllocationVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(SnapshotConflictDto), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AllocationVersionDto>> Initial(
        [FromBody] SolveRequestDto request, CancellationToken ct)
    {
        try
        {
            return Ok(await _allocationService.SolveInitialAsync(request, ct));
        }
        catch (SnapshotConflictException ex)
        {
            return Conflict(ex.Conflict);
        }
    }

    [HttpPost("rearrange")]
    [ProducesResponseType(typeof(AllocationVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(SnapshotConflictDto), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AllocationVersionDto>> Rearrange(
        [FromBody] SolveRequestDto request, CancellationToken ct)
    {
        try
        {
            return Ok(await _allocationService.RearrangeAsync(request, ct));
        }
        catch (SnapshotConflictException ex)
        {
            return Conflict(ex.Conflict);
        }
    }

    [HttpGet("versions/{id:guid}")]
    [ProducesResponseType(typeof(AllocationVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AllocationVersionDto>> GetVersion(Guid id, CancellationToken ct)
    {
        var v = await _allocationService.GetVersionAsync(id, ct);
        return v is null ? NotFound() : Ok(v);
    }

    [HttpGet("versions/latest")]
    [ProducesResponseType(typeof(AllocationVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AllocationVersionDto>> Latest(CancellationToken ct)
    {
        var v = await _allocationService.GetLatestCommittedAsync(ct);
        return v is null ? NotFound() : Ok(v);
    }

    [HttpPost("roads/interrupt")]
    [ProducesResponseType(typeof(RoadEventDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<RoadEventDto>> Interrupt(
        [FromBody] RoadInterruptRequestDto request, CancellationToken ct)
    {
        var evt = await _allocationService.InterruptRoadAsync(request, ct);
        return Ok(evt);
    }

    [HttpPost("roads/reopen")]
    [ProducesResponseType(typeof(RoadEventDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<RoadEventDto>> Reopen(
        [FromBody] RoadReopenRequestDto request, CancellationToken ct)
    {
        var evt = await _allocationService.ReopenRoadAsync(request, ct);
        return Ok(evt);
    }

    [HttpPost("tasks/escalate")]
    [ProducesResponseType(typeof(TaskDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<TaskDto>> Escalate(
        [FromBody] TaskEscalationRequestDto request, CancellationToken ct)
    {
        return Ok(await _allocationService.EscalateTaskAsync(request, ct));
    }

    [HttpPost("tasks/start")]
    [ProducesResponseType(typeof(TaskDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<TaskDto>> Start(
        [FromBody] TaskStartRequestDto request, CancellationToken ct)
    {
        return Ok(await _allocationService.StartTaskAsync(request, ct));
    }

    [HttpPost("tasks/complete")]
    [ProducesResponseType(typeof(TaskDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<TaskDto>> Complete(
        [FromBody] TaskCompleteRequestDto request, CancellationToken ct)
    {
        return Ok(await _allocationService.CompleteTaskAsync(request, ct));
    }

    [HttpGet("versions/{fromId:guid}/diff/{toId:guid}")]
    [ProducesResponseType(typeof(AllocationVersionDiffDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AllocationVersionDiffDto>> Diff(
        Guid fromId, Guid toId, CancellationToken ct)
    {
        try
        {
            return Ok(await _allocationService.DiffVersionsAsync(fromId, toId, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("snapshots/current")]
    [ProducesResponseType(typeof(SnapshotDigestDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SnapshotDigestDto>> CurrentSnapshots(CancellationToken ct)
        => Ok(await _allocationService.GetCurrentDigestsAsync(ct));
}
