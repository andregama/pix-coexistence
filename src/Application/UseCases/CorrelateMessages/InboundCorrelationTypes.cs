namespace ConvivenciaPix.Application.UseCases.CorrelateMessages;

/// <summary>
/// The message-type classifications the System A inbound correlation uses to route each message.
/// </summary>
/// <param name="Allowed">Types processed at all; anything else is skipped.</param>
/// <param name="Primary">
/// Primary/unsolicited inbound initiations (e.g. a received pacs.008 Pix credit) that answer no
/// A/B outbound. Passed through to System B unchanged (no SpiSentMsg correlation).
/// </param>
/// <param name="CorrelateByOriginalMsgId">
/// Message-level responses (e.g. admi.002 rejections) that reference the original message by its
/// MsgId. Correlated via a SpiSentMsg.MsgIdSystemA lookup; on miss, replicated to System B with a
/// warning rather than dead-lettered.
/// </param>
/// <param name="CorrelateByOriginalEndToEndId">
/// Responses (e.g. pacs.004 returns) that reference the original transfer by its OrgnlEndToEndId.
/// Correlated via a SpiSentMsg.IdempotentId lookup on that value; on miss (e.g. TTL-expired),
/// replicated to System B with a warning rather than dead-lettered.
/// </param>
public sealed record InboundCorrelationTypes(
    IReadOnlySet<string> Allowed,
    IReadOnlySet<string> Primary,
    IReadOnlySet<string> CorrelateByOriginalMsgId,
    IReadOnlySet<string> CorrelateByOriginalEndToEndId);
