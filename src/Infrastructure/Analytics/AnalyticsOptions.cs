namespace ConvivenciaPix.Infrastructure.Analytics;

/// <summary>
/// Options for the coexistence analytics reader. Bound from the "Analytics" configuration section.
/// </summary>
public sealed class AnalyticsOptions
{
    /// <summary>
    /// Exclusive cutoff (UTC) for the ConsumedAt / PiResourceId tracking columns
    /// (006_AddConsumedTracking.sql). A row created before this instant predates the tracking and
    /// can never have <c>ConsumedAt</c> set, so a propagated pre-cutoff row is treated as already
    /// consumed by System B. The default covers all of the go-live day (today), i.e. rows from
    /// today and earlier are backfilled as consumed; only rows created on/after the cutoff use the
    /// real <c>ConsumedAt</c>. Override via <c>Analytics:ConsumptionTrackingSince</c> to match the
    /// real deployment instant. Defaults to <see cref="DefaultConsumptionTrackingSince"/>.
    /// </summary>
    public DateTime ConsumptionTrackingSince { get; set; } = DefaultConsumptionTrackingSince;

    /// <summary>
    /// Default cutoff: start of the day after the tracking columns shipped (2026-09-01, UTC
    /// midnight), so every row created on the go-live day (2026-08-31) or earlier counts as consumed.
    /// </summary>
    public static readonly DateTime DefaultConsumptionTrackingSince =
        new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
}
