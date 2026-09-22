namespace ConvivenciaPix.Application.DTOs;

/// <summary>
/// Daily evolution of the transaction <em>count</em> flowing through the coexistence layer for the
/// analytics dashboard: number of transfers (pacs.008) and refunds (pacs.004) per day, split into
/// inbound (received) vs outbound (sent) and success vs failed. Scoped to the [From, To] window
/// (by CreatedAt). The monetary counterpart is <see cref="AmountTimeSeriesDto"/>.
/// </summary>
public sealed record CountTimeSeriesDto(
    DateTime? From,
    DateTime? To,
    IReadOnlyList<CountPointDto> Points);

/// <summary>
/// One day's transfer/refund counts. Failed = row carries any error code (or, for a received
/// pacs.008 credit, the ack-based rule failed); Success = the row cleared. Each figure counts rows,
/// not amounts.
/// </summary>
public sealed record CountPointDto(
    DateTime Day,             // UTC day start
    long ReceivedSuccess,
    long ReceivedFailed,
    long SentSuccess,
    long SentFailed);
