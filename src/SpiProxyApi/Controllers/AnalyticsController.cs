using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;

namespace ConvivenciaPix.SpiProxyApi.Controllers;

/// <summary>
/// Read-only coexistence-flow analytics for the standalone dashboard (frontend/analytics).
/// Anonymous + CORS-enabled by design — intended for local/homologation use only, not exposed to Bacen.
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableCors("analytics-frontend")]
[Route("api/v1/analytics")]
public sealed class AnalyticsController : ControllerBase
{
    private readonly ICoexistenceAnalyticsReader _reader;

    public AnalyticsController(ICoexistenceAnalyticsReader reader) => _reader = reader;

    /// <summary>
    /// Aggregated coexistence summary: the Received → Propagated → Consumed funnel, error counts,
    /// correlation-source split, per-message-type breakdown, discrepancies by field and recent errors.
    /// Optional <paramref name="from"/>/<paramref name="to"/> (UTC) bound the window by CreatedAt/DetectedAt.
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(CoexistenceSummaryDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<CoexistenceSummaryDto>> GetSummary(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken cancellationToken)
    {
        var summary = await _reader.GetSummaryAsync(from?.ToUniversalTime(), to?.ToUniversalTime(), cancellationToken);
        return Ok(summary);
    }
}
