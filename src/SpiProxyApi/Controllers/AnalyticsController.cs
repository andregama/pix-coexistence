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

    /// <summary>
    /// Daily evolution of the propagation rate (propagated ÷ received) over the optional
    /// <paramref name="from"/>/<paramref name="to"/> (UTC) window.
    /// </summary>
    [HttpGet("timeseries")]
    [ProducesResponseType(typeof(PropagationTimeSeriesDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<PropagationTimeSeriesDto>> GetTimeSeries(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken cancellationToken)
    {
        var series = await _reader.GetPropagationTimeSeriesAsync(from?.ToUniversalTime(), to?.ToUniversalTime(), cancellationToken);
        return Ok(series);
    }

    /// <summary>
    /// Daily evolution of propagated error counts (System A vs System B error codes) over the optional
    /// <paramref name="from"/>/<paramref name="to"/> (UTC) window.
    /// </summary>
    [HttpGet("errors-timeseries")]
    [ProducesResponseType(typeof(ErrorTimeSeriesDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ErrorTimeSeriesDto>> GetErrorTimeSeries(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken cancellationToken)
    {
        var series = await _reader.GetErrorTimeSeriesAsync(from?.ToUniversalTime(), to?.ToUniversalTime(), cancellationToken);
        return Ok(series);
    }

    /// <summary>
    /// Daily evolution of summed amounts (transfer + withdrawal) over the optional
    /// <paramref name="from"/>/<paramref name="to"/> (UTC) window, split into received/sent and
    /// success/failed.
    /// </summary>
    [HttpGet("amounts-timeseries")]
    [ProducesResponseType(typeof(AmountTimeSeriesDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AmountTimeSeriesDto>> GetAmountTimeSeries(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken cancellationToken)
    {
        var series = await _reader.GetAmountTimeSeriesAsync(from?.ToUniversalTime(), to?.ToUniversalTime(), cancellationToken);
        return Ok(series);
    }
}
