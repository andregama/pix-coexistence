namespace ConvivenciaPix.Application.DTOs;

/// <summary>
/// A Bacen inbound response already correlated and transformed to System B's expectations by
/// the correlate worker, ready for the proxy worker to sign and enqueue. Carried inside a
/// <see cref="KafkaEnvelope"/> (base64 JSON) on the spi.systemb.responses topic.
/// </summary>
public sealed record SystemBInboundReadyDto(
    string IdempotentId,
    string MsgType,
    string TransformedXml,
    DateTimeOffset OccurredAt);
