namespace ConvivenciaPix.Application.DTOs;

/// <summary>
/// Daily evolution of propagated error counts for the analytics dashboard: for each day, how many
/// messages carry a System A error code vs a System B error code (inbound and outbound combined).
/// Scoped to the [From, To] window (by CreatedAt).
/// </summary>
public sealed record ErrorTimeSeriesDto(
    DateTime? From,
    DateTime? To,
    IReadOnlyList<ErrorPointDto> Points);

/// <summary>One day's error figures. Counts are inbound + outbound rows carrying that system's error code.</summary>
public sealed record ErrorPointDto(
    DateTime Day,           // UTC day start
    long SystemAErrors,     // SystemAErrorCode IS NOT NULL, created that day
    long SystemBErrors);    // SystemBErrorCode IS NOT NULL, created that day
