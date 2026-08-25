namespace ConvivenciaPix.Infrastructure.Parsing;

/// <summary>
/// Configurable rules for <see cref="InboundResponseTransformer"/>. Each rule reads a value from
/// the same XPath in both pacs.008 requests; when System A and System B differ, the response node
/// at <see cref="ResponseTransformRule.ResponseTargetXPath"/> is set to System B's value.
/// Defaults are seeded in code, so the transformer works with zero configuration.
/// </summary>
public sealed class ResponseTransformOptions
{
    public IReadOnlyList<ResponseTransformRule> Rules { get; set; } = DefaultRules;

    public static readonly IReadOnlyList<ResponseTransformRule> DefaultRules =
    [
        // System A/B use independent EndToEndIds; Bacen's pacs.002 references A's via
        // OrgnlEndToEndId, so rewrite it to B's so System B recognises the response.
        new ResponseTransformRule
        {
            Name = "EndToEndId",
            SentValueXPath = "//*[local-name()='CdtTrfTxInf']/*[local-name()='PmtId']/*[local-name()='EndToEndId']",
            ResponseTargetXPath = "//*[local-name()='TxInfAndSts']/*[local-name()='OrgnlEndToEndId']"
        },
        // Initiation form (e.g. DICT vs MANU) carried in LclInstrm/Prtry. Present in the
        // response only when it echoes the original transaction reference; skipped otherwise.
        new ResponseTransformRule
        {
            Name = "InitiationForm",
            SentValueXPath = "//*[local-name()='LclInstrm']/*[local-name()='Prtry']",
            ResponseTargetXPath = "//*[local-name()='OrgnlTxRef']//*[local-name()='LclInstrm']/*[local-name()='Prtry']"
        },
        // Original message id back-link. Status reports / receipts / rejections that answer a message
        // System B originated (pain.012→pain.011, pain.014→pain.013, camt.029→camt.055, camt.025,
        // admi.002) carry the answered message's MsgId in OrgnlMsgId. A and B mint MsgIds
        // independently, so rewrite the reference from A's header MsgId to B's. Self-guarding: fires
        // only when the response actually carries OrgnlMsgId. (Confirm exact carriers vs. the Catálogo.)
        new ResponseTransformRule
        {
            Name = "OriginalMsgId",
            SentValueXPath = "//*[local-name()='AppHdr']/*[local-name()='MsgId'] | //*[local-name()='GrpHdr']/*[local-name()='MsgId']",
            ResponseTargetXPath = "//*[local-name()='OrgnlMsgId']"
        },
        // Investigation case id (camt.055 ↔ camt.029). The case identification is per-system; rewrite
        // the response's Case/Id from A's value to B's. Confirm the exact element against the Catálogo.
        new ResponseTransformRule
        {
            Name = "InvestigationCaseId",
            SentValueXPath = "//*[local-name()='Case']/*[local-name()='Id']",
            ResponseTargetXPath = "//*[local-name()='Case']/*[local-name()='Id']"
        },
    ];
}

public sealed class ResponseTransformRule
{
    /// <summary>Human-readable name for logging/diagnostics.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>XPath (namespace-agnostic via local-name()) read from both pacs.008 requests.</summary>
    public string SentValueXPath { get; set; } = string.Empty;

    /// <summary>XPath of the node in the response whose text is replaced with System B's value.</summary>
    public string ResponseTargetXPath { get; set; } = string.Empty;
}
