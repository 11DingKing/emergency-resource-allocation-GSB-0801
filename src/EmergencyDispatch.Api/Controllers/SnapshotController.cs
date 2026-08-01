namespace EmergencyDispatch.Api.Controllers;

using EmergencyDispatch.Api.Contracts;
using EmergencyDispatch.Infrastructure;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Exposes the current world snapshot and its deterministic digest. A client fetches this,
/// then submits a solve/replan bound to the returned <c>snapshotVersion</c>. This controller
/// holds no scheduling logic; it only projects state through the allocation service.
/// </summary>
[ApiController]
[Route("api/snapshot")]
public sealed class SnapshotController : ControllerBase
{
    private readonly IAllocationService _service;

    public SnapshotController(IAllocationService service) => _service = service;

    /// <summary>Return the canonical world snapshot and the digest to bind a solve to.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(SnapshotDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var snapshot = await _service.GetCurrentSnapshotAsync(ct);
        return Ok(SnapshotDto.From(snapshot));
    }
}
