namespace ConvivenciaPix.V2.Domain.Messages;

/// <summary>
/// One message a system sent through the proxy. Recorded before the message is forwarded so that a
/// response from Bacen can always be routed back to its sender.
/// </summary>
public sealed record SpiSentRoute(
    string MsgId,
    SystemName SystemName,
    string MsgType,
    string? TxKey,
    string? EchoDataHash);
