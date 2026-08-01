namespace EmergencyDispatch.Api.Controllers;

using EmergencyDispatch.Api.Contracts;
using EmergencyDispatch.Infrastructure;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Allocation endpoints. This controller contains no scheduling logic whatsoever — it only
/// validates input, delegates to <see cref="IAllocationService"/> and maps results to DTOs.
/// The allocation algorithm lives behind <c>IAllocationSolver</c> in a separate assembly.
/// </summary>
[ApiController]
[Route("api/allocations")]
public sealed class AllocationsController : ControllerBase
{
    private readonly IAllocationService _service;

    public AllocationsController(IAllocationService service) => _service = service;

    /// <summary>Produce an initial allocation for the given input version (idempotent).</summary>
    [HttpPost("solve")]
    [ProducesResponseType(typeof(AllocationVersionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(AllocationVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Solve([FromBody] SolveRequestDto body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.InputVersion))
        {
            return BadRequest(new { error = "inputVersion is required." });
        }

        var result = await _service.SolveAsync(
            new SolveRequest { InputVersion = body.InputVersion, IsReplan = false }, ct);

        return Respond(result);
    }

    /// <summary>
    /// Replan for the given input version. Allows danger-escalated preemption of in-progress
    /// tasks per the domain rules; still idempotent on input version.
    /// </summary>
    [HttpPost("replan")]
    [ProducesResponseType(typeof(AllocationVersionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(AllocationVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Replan([FromBody] SolveRequestDto body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.InputVersion))
        {
            return BadRequest(new { error = "inputVersion is required." });
        }

        var result = await _service.SolveAsync(
            new SolveRequest { InputVersion = body.InputVersion, IsReplan = true }, ct);

        return Respond(result);
    }

    /// <summary>Fetch a specific allocation version by its monotonic number.</summary>
    [HttpGet("{versionNumber:int}")]
    [ProducesResponseType(typeof(AllocationVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(int versionNumber, CancellationToken ct)
    {
        var version = await _service.GetVersionAsync(versionNumber, ct);
        return version is null ? NotFound() : Ok(AllocationVersionDto.From(version));
    }

    /// <summary>Fetch the latest allocation version.</summary>
    [HttpGet("latest")]
    [ProducesResponseType(typeof(AllocationVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Latest(CancellationToken ct)
    {
        var version = await _service.GetLatestAsync(ct);
        return version is null ? NotFound() : Ok(AllocationVersionDto.From(version));
    }

    /// <summary>Return the ordered audit trail explaining a version's decisions.</summary>
    [HttpGet("{versionNumber:int}/explanation")]
    [ProducesResponseType(typeof(ExplanationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Explain(int versionNumber, CancellationToken ct)
    {
        var explanation = await _service.ExplainAsync(versionNumber, ct);
        return explanation is null ? NotFound() : Ok(ExplanationDto.From(explanation));
    }

    private IActionResult Respond(AllocationResult result)
    {
        var dto = AllocationVersionDto.From(result.Version);
        if (result.WasExisting)
        {
            // Idempotent replay: the plan already existed, so nothing new was created.
            return Ok(dto);
        }
        return CreatedAtAction(nameof(Get), new { versionNumber = dto.VersionNumber }, dto);
    }
}
