using ConvivenciaPix.V2.Domain.Routing;

namespace ConvivenciaPix.V2.Application.Parsing;

/// <summary>
/// One transaction inside a message. SPI messages may carry several (a pacs.008, pacs.002 or
/// pacs.004 can hold many), each possibly belonging to a different system.
/// </summary>
/// <param name="OwnSideDocument">
/// Document of the party whose agent is the bank the message is addressed to — the bank's own
/// customer, for messages that travel in both directions. Null when it cannot be told.
/// </param>
public sealed record ParsedTransaction(
    IReadOnlyList<TxKey> TxKeys,
    string? CreditorDocument,
    string? DebtorDocument,
    string? OwnSideDocument)
{
    public static readonly ParsedTransaction Empty = new([], null, null, null);
}

/// <summary>
/// Everything routing needs from an SPI message, extracted in one pass. Every field is optional:
/// a message that cannot be parsed still yields an instance (with <see cref="UnknownType"/>) so
/// that a malformed message can never block the stream.
/// </summary>
public sealed record ParsedSpiMessage(
    string MsgType,
    string? MsgId,
    IReadOnlyList<string> OriginalMsgRefs,
    IReadOnlyList<ParsedTransaction> Transactions,
    string? EchoData)
{
    public const string UnknownType = "unknown";

    public static readonly ParsedSpiMessage Unparseable = new(UnknownType, null, [], [], null);

    /// <summary>A message with a single transaction (the common case).</summary>
    public static ParsedSpiMessage Single(
        string msgType,
        string? msgId,
        IReadOnlyList<string> originalMsgRefs,
        IReadOnlyList<TxKey> txKeys,
        string? creditorDocument = null,
        string? debtorDocument = null,
        string? echoData = null,
        string? ownSideDocument = null) =>
        new(msgType, msgId, originalMsgRefs,
            [new ParsedTransaction(txKeys, creditorDocument, debtorDocument, ownSideDocument)], echoData);

    public bool IsUnknown => MsgType == UnknownType;

    /// <summary>True when the content is not well-formed XML at all (as opposed to an unknown type).</summary>
    public bool IsUnparseable => ReferenceEquals(this, Unparseable);

    /// <summary>Keys of every transaction in the message, in document order.</summary>
    public IReadOnlyList<TxKey> TxKeys { get; } = Transactions.SelectMany(t => t.TxKeys).Distinct().ToList();

    /// <summary>First transaction key, stored alongside the message for troubleshooting.</summary>
    public string? PrimaryTxKey => TxKeys.Count > 0 ? TxKeys[0].Value : null;
}

public interface ISpiMessageParser
{
    /// <summary>Never throws; returns <see cref="ParsedSpiMessage.Unparseable"/> for malformed XML.</summary>
    ParsedSpiMessage Parse(ReadOnlyMemory<byte> xml);
}
