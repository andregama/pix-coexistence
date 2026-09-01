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
        // Original message id back-link (flat carriers). Status reports / rejections that answer a
        // message System B originated (pain.012→pain.011, pain.014→pain.013, camt.029→camt.055,
        // admi.002) carry the answered message's MsgId in a flat <OrgnlMsgId>. A and B mint MsgIds
        // independently, so rewrite the reference from A's header MsgId to B's. The [not(*)] guard
        // keeps this from corrupting camt.025's nested <OrgnlMsgId><MsgId> (handled by
        // TrackerOriginalMsgId below). Self-guarding: fires only when the flat node is present.
        new ResponseTransformRule
        {
            Name = "OriginalMsgId",
            SentValueXPath = "//*[local-name()='AppHdr']/*[local-name()='MsgId'] | //*[local-name()='GrpHdr']/*[local-name()='MsgId']",
            ResponseTargetXPath = "//*[local-name()='OrgnlMsgId'][not(*)]"
        },
        // camt.025 (Rct) receipt for a trck.002 nests the back-link as <OrgnlMsgId><MsgId>. Rewrite it
        // from A's trck.002 message id (GrpHdr/MsgId == BizMsgIdr) to B's so System B recognises the
        // receipt. Fires only when the nested node is present (i.e. on camt.025).
        new ResponseTransformRule
        {
            Name = "TrackerOriginalMsgId",
            SentValueXPath = "//*[local-name()='GrpHdr']/*[local-name()='MsgId']",
            ResponseTargetXPath = "//*[local-name()='OrgnlMsgId']/*[local-name()='MsgId']"
        },
        // AppHdr addressing. The receipt's To is the responder PSP's ISPB; when System A and System B
        // present as different ISPBs, rewrite the response's To from A's sender ISPB to B's (read from
        // each side's request AppHdr/Fr). Fires only when the ISPBs differ. Re-signing happens at
        // delivery (PropagateResponse).
        new ResponseTransformRule
        {
            Name = "ResponderIspb",
            SentValueXPath = "//*[local-name()='AppHdr']/*[local-name()='Fr']//*[local-name()='Id']",
            ResponseTargetXPath = "//*[local-name()='AppHdr']/*[local-name()='To']//*[local-name()='Id']"
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
