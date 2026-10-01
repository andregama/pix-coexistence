namespace ConvivenciaPix.V2.Domain.Routing;

/// <summary>Names of the resolvers that can decide a route; persisted as <c>RouteSource</c>.</summary>
public static class RouteSources
{
    public const string SentByOriginalMsgId = "SentByOriginalMsgId";
    public const string SentByPiResourceId = "SentByPiResourceId";
    public const string TxAffinity = "TxAffinity";
    public const string DocumentRegistry = "DocumentRegistry";
    public const string EchoData = "EchoData";
    public const string Broadcast = "Broadcast";
    public const string Default = "Default";

    /// <summary>
    /// Not a resolver: the message carried transactions of different systems and each system
    /// receives its own copy with only its transactions.
    /// </summary>
    public const string Split = "Split";

    /// <summary>
    /// Not a resolver: the message carried transactions of different systems and was delivered whole
    /// (rules configured not to split).
    /// </summary>
    public const string Mixed = "Mixed";

    /// <summary>Not a resolver: Bacen's signature did not verify; the message was not delivered.</summary>
    public const string InvalidSignature = "InvalidSignature";

    /// <summary>Not a resolver: the content is not XML; it cannot be signed, so it was not delivered.</summary>
    public const string Unreadable = "Unreadable";
}

/// <summary>The transactions (by position in the message) one system receives from a split message.</summary>
public sealed record RoutingPart(SystemName System, IReadOnlyList<int> Transactions);

/// <summary>A transaction key to bind to the system that now handles that transaction.</summary>
public readonly record struct TxBinding(TxKey Key, SystemName System);

/// <summary>Which system(s) receive an inbound message, and which resolver decided it.</summary>
public sealed record RoutingDecision(IReadOnlyList<SystemName> Targets, string Source)
{
    private static readonly SystemName[] Both = [SystemName.SystemA, SystemName.SystemB];

    /// <summary>Transactions this message binds to a system, so later messages about them follow.</summary>
    public IReadOnlyList<TxBinding> Bindings { get; init; } = [];

    /// <summary>
    /// Set only for a split message: what each target receives. Empty means every target receives
    /// the whole message.
    /// </summary>
    public IReadOnlyList<RoutingPart> Parts { get; init; } = [];

    public static RoutingDecision To(SystemName system, string source) => new([system], source);

    public static RoutingDecision ToBoth(string source) => new(Both, source);

    /// <summary>Compact form persisted in <c>SpiInboundMsg.Targets</c> ("SystemA", "SystemA,SystemB").</summary>
    public string TargetsText => string.Join(",", Targets);
}
