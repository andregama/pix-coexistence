namespace ConvivenciaPix.Application.DTOs;

/// <summary>
/// Aggregated coexistence-flow analytics over the inbound (SpiReceivedMsg), outbound
/// (SpiSentMsg) and discrepancy (SpiDiscrepancies) tables, for the local analytics dashboard.
/// All counts are scoped to the [From, To] window (by CreatedAt / DetectedAt).
/// </summary>
public sealed record CoexistenceSummaryDto(
    DateTime? From,
    DateTime? To,
    DateTime GeneratedAt,
    InboundFunnelDto Inbound,
    OutboundStatsDto Outbound,
    ErrorStatsDto Errors,
    IReadOnlyList<LabelCountDto> CorrelationSource,
    IReadOnlyList<MsgTypeBreakdownDto> ByMsgType,
    IReadOnlyList<OutboundMsgTypeBreakdownDto> OutboundByMsgType,
    ReplicationLatencyDto Latency,
    IReadOnlyList<LabelCountDto> DiscrepanciesByField,
    IReadOnlyList<RecentErrorDto> RecentErrors);

/// <summary>The Received → Propagated → Consumed funnel for inbound (SPI→PSP) messages.</summary>
public sealed record InboundFunnelDto(
    long ReceivedFromA,       // XmlMsgSystemA IS NOT NULL
    long PropagatedToB,       // XmlMsgSystemB IS NOT NULL (signed & enqueued for B)
    long ConsumedByB,         // ConsumedAt IS NOT NULL (pulled + acked)
    long AwaitingConsumption, // XmlMsgSystemB IS NOT NULL AND ConsumedAt IS NULL
    long PropagationGap);     // XmlMsgSystemA IS NOT NULL AND XmlMsgSystemB IS NULL

/// <summary>Outbound (PSP→SPI) correlation stats.</summary>
public sealed record OutboundStatsDto(
    long Total,
    long CorrelatedPairs);    // MsgIdSystemA IS NOT NULL AND MsgIdSystemB IS NOT NULL

public sealed record ErrorStatsDto(
    long InboundSystemAErrors,
    long InboundSystemBErrors,
    long OutboundSystemAErrors,
    long OutboundSystemBErrors,
    long Discrepancies);

public sealed record LabelCountDto(string Label, long Count);

public sealed record MsgTypeBreakdownDto(
    string MsgType,
    long ReceivedFromA,
    long PropagatedToB,
    long ConsumedByB);

/// <summary>Outbound (PSP→SPI) sent/correlated counts per message type, incl. trck.002.</summary>
public sealed record OutboundMsgTypeBreakdownDto(
    string MsgType,
    long Total,
    long Correlated);   // MsgIdSystemA IS NOT NULL AND MsgIdSystemB IS NOT NULL

/// <summary>Time taken through the replication flow (from stored timestamps).</summary>
public sealed record ReplicationLatencyDto(
    LatencyStatsDto InboundEndToEnd,      // SpiReceivedMsg: ConsumedAt − CreatedAt
    LatencyStatsDto OutboundCorrelation); // SpiSentMsg (complete pair): UpdatedAt − CreatedAt

/// <summary>Latency distribution in milliseconds; all zero when <see cref="Count"/> is 0.</summary>
public sealed record LatencyStatsDto(
    long Count,
    double AvgMs,
    double P50Ms,
    double P95Ms,
    double MaxMs)
{
    public static readonly LatencyStatsDto Empty = new(0, 0, 0, 0, 0);
}

public sealed record RecentErrorDto(
    string IdempotentId,
    string MsgType,
    string Direction,   // "Inbound" or "Outbound"
    string System,      // "A" or "B"
    string ErrorCode,
    DateTime At);
