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

public sealed record RecentErrorDto(
    string IdempotentId,
    string MsgType,
    string Direction,   // "Inbound" or "Outbound"
    string System,      // "A" or "B"
    string ErrorCode,
    DateTime At);
