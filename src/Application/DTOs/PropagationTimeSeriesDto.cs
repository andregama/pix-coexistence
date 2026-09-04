namespace ConvivenciaPix.Application.DTOs;

/// <summary>
/// Daily evolution of the propagation rate for the analytics dashboard: for messages received from
/// System A each day, the share propagated to System B. Scoped to the [From, To] window (by CreatedAt).
/// </summary>
public sealed record PropagationTimeSeriesDto(
    DateTime? From,
    DateTime? To,
    IReadOnlyList<PropagationPointDto> Points);

/// <summary>One day's propagation figures. <see cref="PropagatedPct"/> is 0..100 (0 when Received is 0).</summary>
public sealed record PropagationPointDto(
    DateTime Day,        // UTC day start
    long Received,       // XmlMsgSystemA IS NOT NULL, created that day
    long Propagated,     // of those, XmlMsgSystemB IS NOT NULL
    double PropagatedPct);
