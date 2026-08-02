using EmergencyAllocation.Infrastructure.Services;
using EmergencyAllocation.Infrastructure.Services.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace EmergencyAllocation.Api.Controllers;

[ApiController]
[Route("api/allocations")]
public class AllocationsController : ControllerBase
{
    private readonly IAllocationService _allocationService;

    public AllocationsController(IAllocationService allocationService)
    {
        _allocationService = allocationService;
    }

    [HttpPost("initial")]
    [ProducesResponseType(typeof(AllocationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(SnapshotConflictResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SolveInitial([FromBody] InitialSolveRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.InputVersion))
        {
            return BadRequest("inputVersion is required.");
        }

        try
        {
            var result = await _allocationService.SolveInitialAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (SnapshotConflictException ex)
        {
            return Conflict(ex.Response);
        }
    }

    [HttpPost("rearrange")]
    [ProducesResponseType(typeof(AllocationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(SnapshotConflictResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Rearrange([FromBody] RearrangeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.InputVersion))
        {
            return BadRequest("inputVersion is required.");
        }

        try
        {
            var result = await _allocationService.RearrangeAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (SnapshotConflictException ex)
        {
            return Conflict(ex.Response);
        }
    }

    [HttpGet("latest")]
    [ProducesResponseType(typeof(AllocationResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLatest(CancellationToken cancellationToken)
    {
        var result = await _allocationService.GetLatestCommittedAsync(cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("by-input/{inputVersion}")]
    [ProducesResponseType(typeof(AllocationResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByInputVersion(string inputVersion, CancellationToken cancellationToken)
    {
        var result = await _allocationService.GetByInputVersionAsync(inputVersion, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("{versionId:guid}")]
    [ProducesResponseType(typeof(AllocationResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetById(Guid versionId, CancellationToken cancellationToken)
    {
        var result = await _allocationService.GetVersionAsync(versionId, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("{versionId:guid}/explain")]
    [ProducesResponseType(typeof(ExplanationDto[]), StatusCodes.Status200OK)]
    public async Task<IActionResult> Explain(Guid versionId, CancellationToken cancellationToken)
    {
        var result = await _allocationService.GetVersionAsync(versionId, cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        return Ok(result.Explanations);
    }
}
