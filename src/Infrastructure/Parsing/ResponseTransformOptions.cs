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
