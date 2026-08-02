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
    public async Task<ActionResult<AllocationVersionDto>> Initial(
        [FromBody] SolveRequestDto request, CancellationToken ct)
    {
        var result = await _allocationService.SolveInitialAsync(request, ct);
        return Ok(result);
    }

    [HttpPost("rearrange")]
    [ProducesResponseType(typeof(AllocationVersionDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AllocationVersionDto>> Rearrange(
        [FromBody] SolveRequestDto request, CancellationToken ct)
    {
        var result = await _allocationService.RearrangeAsync(request, ct);
        return Ok(result);
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
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Interrupt(
        [FromBody] RoadInterruptRequestDto request, CancellationToken ct)
    {
        await _allocationService.InterruptRoadAsync(request, ct);
        return NoContent();
    }

    [HttpPost("roads/reopen")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Reopen(
        [FromBody] RoadReopenRequestDto request, CancellationToken ct)
    {
        await _allocationService.ReopenRoadAsync(request, ct);
        return NoContent();
    }
}
