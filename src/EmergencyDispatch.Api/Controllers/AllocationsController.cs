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

    /// <summary>Produce an initial allocation for the given input version + snapshot (idempotent).</summary>
    [HttpPost("solve")]
    [ProducesResponseType(typeof(AllocationVersionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(AllocationVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ConflictDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Solve([FromBody] SolveRequestDto body, CancellationToken ct)
    {
        if (Validate(body) is { } bad) return bad;

        var result = await _service.SolveAsync(
            new SolveRequest
            {
                InputVersion = body.InputVersion,
                SnapshotVersion = body.SnapshotVersion,
                IsReplan = false,
            }, ct);

        return Respond(result);
    }

    /// <summary>
    /// Replan for the given input version + snapshot. Allows danger-escalated preemption of
    /// in-progress tasks per the domain rules; still idempotent on input version, and still
    /// conflicts (409) when the same input version is submitted against a different snapshot.
    /// </summary>
    [HttpPost("replan")]
    [ProducesResponseType(typeof(AllocationVersionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(AllocationVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ConflictDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Replan([FromBody] SolveRequestDto body, CancellationToken ct)
    {
        if (Validate(body) is { } bad) return bad;

        var result = await _service.SolveAsync(
            new SolveRequest
            {
                InputVersion = body.InputVersion,
                SnapshotVersion = body.SnapshotVersion,
                IsReplan = true,
            }, ct);

        return Respond(result);
    }

    private IActionResult? Validate(SolveRequestDto body)
    {
        if (string.IsNullOrWhiteSpace(body.InputVersion))
        {
            return BadRequest(new { error = "inputVersion is required." });
        }
        if (string.IsNullOrWhiteSpace(body.SnapshotVersion))
        {
            return BadRequest(new { error = "snapshotVersion is required; fetch it from GET /api/snapshot." });
        }
        return null;
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
        if (result.Conflict is { } conflict)
        {
            // Same input version bound to a different snapshot: never replay the old plan.
            return Conflict(ConflictDto.From(conflict));
        }

        var dto = AllocationVersionDto.From(result.Version!);
        if (result.WasExisting)
        {
            // Idempotent replay: identical input version AND snapshot, so nothing new was created.
            return Ok(dto);
        }
        return CreatedAtAction(nameof(Get), new { versionNumber = dto.VersionNumber }, dto);
    }
}
