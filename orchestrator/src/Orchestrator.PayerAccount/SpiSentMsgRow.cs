namespace Orchestrator.PayerAccount;

/// <summary>The subset of a <c>dbo.SpiSentMsg</c> row needed to resolve the payer account.</summary>
public sealed record SpiSentMsgRow(
    string IdempotentId,
    string MsgType,
    string? XmlMsgSystemA,
    string? XmlMsgSystemB,
    string? OriginalMsgIdempotentId);
