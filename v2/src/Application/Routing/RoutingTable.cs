using ConvivenciaPix.V2.Domain.Routing;
using Microsoft.Extensions.Options;

namespace ConvivenciaPix.V2.Application.Routing;

/// <summary>Message type → resolver chain: built-in defaults overlaid with configured rules.</summary>
public sealed class RoutingTable
{
    private static readonly string[] ResponseChain =
        [RouteSources.SentByOriginalMsgId, RouteSources.TxAffinity, RouteSources.Default];

    private static readonly string[] ReportChain = [RouteSources.SentByOriginalMsgId, RouteSources.Broadcast];

    private static readonly string[] CustomerChain =
        [RouteSources.SentByOriginalMsgId, RouteSources.TxAffinity, RouteSources.DocumentRegistry, RouteSources.Default];

    /// <summary>
    /// Built-in rules, checked against the examples and schemas of the Bacen SPI catalog 5.13.1.
    /// </summary>
    public static IReadOnlyDictionary<string, RoutingRule> Defaults { get; } =
        new Dictionary<string, RoutingRule>(StringComparer.OrdinalIgnoreCase)
        {
            // Received transfer: the creditor is our customer. Affinity first so that a registry
            // change in the middle of a transaction cannot split it between the systems.
            ["pacs.008"] = Rule(DocumentParty.Creditor,
                RouteSources.TxAffinity, RouteSources.DocumentRegistry, RouteSources.Default),

            // Status of a payment or return. The SPI pacs.002 names no original message, only the
            // instruction (OrgnlInstrId: EndToEndId or RtrId), so it follows the transaction.
            ["pacs.002"] = Rule(ResponseChain),
            // Return of a payment we sent: follows the original payment (OrgnlEndToEndId).
            ["pacs.004"] = Rule(RouteSources.TxAffinity, RouteSources.Default),

            // Receipt of a trck.002 (names it in RctDtls/OrgnlMsgId/MsgId).
            ["camt.025"] = Rule(ResponseChain),

            // Cancellation of a scheduled payment and its resolution: both only name the payment.
            ["camt.055"] = Rule(ResponseChain),
            ["camt.029"] = Rule(ResponseChain),

            ["pibr.002"] = Rule(RouteSources.EchoData, RouteSources.Default),

            // RltdRef/Ref is the rejected message's id or, when it could not be read, its PI-ResourceId.
            ["admi.002"] = Rule(
                RouteSources.SentByOriginalMsgId, RouteSources.SentByPiResourceId, RouteSources.Default),

            // Not addressed to a customer: both systems need to see it.
            ["admi.004"] = Rule(RouteSources.Broadcast),

            // Account reports answer a camt.060 (its MsgId comes back in Rpt/Stmt/Ntfctn Id); the
            // ones Bacen sends on its own initiative carry zeros there and go to both systems.
            ["camt.052"] = Rule(ReportChain),
            ["camt.053"] = Rule(ReportChain),
            ["camt.054"] = Rule(ReportChain),

            // Participant data: reda.016 answers a reda.014/022/031 (MsgHdr/OrgnlBizInstr/MsgId);
            // camt.014, reda.017 and reda.041 name no request.
            ["camt.014"] = Rule(ReportChain),
            ["reda.*"] = Rule(ReportChain),
            ["trck.*"] = Rule(ReportChain),

            // Pix Automático. pain.009 and pain.013 reach the payer's PSP: the debtor is our customer.
            ["pain.009"] = Rule(DocumentParty.Debtor, CustomerChain),
            ["pain.013"] = Rule(DocumentParty.Debtor, CustomerChain),
            // pain.011 and pain.012 travel in both directions; our customer is on our side of the mandate.
            ["pain.011"] = Rule(DocumentParty.OwnSide, CustomerChain),
            ["pain.012"] = Rule(DocumentParty.OwnSide, CustomerChain),
            // Status of a pain.013 we sent (its OrgnlMsgId is a zeros placeholder).
            ["pain.014"] = Rule(ResponseChain),
        };

    private readonly Dictionary<string, RoutingRule> _rules;
    private readonly RoutingRule _unknown;

    public RoutingTable(IOptions<RoutingOptions> options)
    {
        var configured = options.Value;
        _rules = new Dictionary<string, RoutingRule>(Defaults, StringComparer.OrdinalIgnoreCase);
        foreach (var (type, rule) in configured.Rules)
        {
            if (rule.Chain.Count == 0)
                throw new InvalidOperationException($"Routing:Rules:{type} has an empty Chain.");
            _rules[type] = rule;
        }

        _unknown = new RoutingRule
        {
            Chain = configured.UnknownTypeChain.Count > 0 ? configured.UnknownTypeChain : [.. ReportChain]
        };
    }

    public IEnumerable<RoutingRule> AllRules => _rules.Values.Append(_unknown);

    /// <summary>Exact type first, then the family wildcard ("reda.*"), then the unknown-type chain.</summary>
    public RoutingRule For(string msgType)
    {
        if (_rules.TryGetValue(msgType, out var exact))
            return exact;

        var dot = msgType.IndexOf('.');
        if (dot > 0 && _rules.TryGetValue(msgType[..dot] + ".*", out var family))
            return family;

        return _unknown;
    }

    private static RoutingRule Rule(params string[] chain) => new() { Chain = [.. chain] };

    private static RoutingRule Rule(DocumentParty party, params string[] chain) =>
        new() { Chain = [.. chain], DocumentParty = party };
}
