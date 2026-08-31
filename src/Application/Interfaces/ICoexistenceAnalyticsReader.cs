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
}
