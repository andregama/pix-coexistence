namespace ConvivenciaPix.V2.Domain.Messages;

/// <summary>
/// A message pulled from Bacen, stored exactly as received (with Bacen's signature) together with
/// its routing decision.
/// </summary>
public sealed record SpiInboundMsg(
    string PiResourceId,
    string? MsgId,
    string MsgType,
    string? TxKey,
    string? OriginalMsgId,
    string RouteSource,
    string Targets,
    byte[] Payload);

/// <summary>
/// What one system receives for an inbound message: the content signed by the bank's HSM — the
/// whole message, or only that system's transactions when the message was split.
/// </summary>
public sealed record InboundDelivery(SystemName System, byte[] Payload);

/// <summary>A message leased to one of a system's pull streams.</summary>
public sealed record DeliveryItem(string PiResourceId, byte[] Payload);

/// <summary>Audit event written to the outbox and later published to Kafka (ids only, no payload).</summary>
public sealed record AuditEvent(string Topic, string Key, string PayloadJson);
