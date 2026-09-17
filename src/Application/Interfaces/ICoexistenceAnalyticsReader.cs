using ConvivenciaPix.Application.DTOs;

namespace ConvivenciaPix.Application.Interfaces;

/// <summary>
/// Read-only gateway that computes aggregate coexistence-flow analytics for the local
/// dashboard. Implemented in Infrastructure over the EF <c>CoexistenceDbContext</c>.
/// </summary>
public interface ICoexistenceAnalyticsReader
{
    /// <summary>
    /// Builds the coexistence summary over the optional [<paramref name="from"/>, <paramref name="to"/>]
    /// window (UTC). Null bounds mean unbounded on that side.
    /// </summary>
    Task<CoexistenceSummaryDto> GetSummaryAsync(
        DateTime? from, DateTime? to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Daily evolution of the propagation rate (propagated ÷ received) over the optional
    /// [<paramref name="from"/>, <paramref name="to"/>] window (UTC). Days with no received messages
    /// produce no point.
    /// </summary>
    Task<PropagationTimeSeriesDto> GetPropagationTimeSeriesAsync(
        DateTime? from, DateTime? to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Daily evolution of propagated error counts (System A vs System B error codes) over the optional
    /// [<paramref name="from"/>, <paramref name="to"/>] window (UTC). Days with no errors produce no point.
    /// </summary>
    Task<ErrorTimeSeriesDto> GetErrorTimeSeriesAsync(
        DateTime? from, DateTime? to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Daily evolution of summed amounts (transfer + withdrawal) over the optional
    /// [<paramref name="from"/>, <paramref name="to"/>] window (UTC), split into received vs sent and
    /// success vs failed. Days with no amounts produce no point.
    /// </summary>
    Task<AmountTimeSeriesDto> GetAmountTimeSeriesAsync(
        DateTime? from, DateTime? to, CancellationToken cancellationToken = default);
}
