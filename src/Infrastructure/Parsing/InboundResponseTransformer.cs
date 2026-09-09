using ConvivenciaPix.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Xml;
using System.Xml.XPath;

namespace ConvivenciaPix.Infrastructure.Parsing;

/// <summary>
/// Rewrites System-A-specific fields in a Bacen inbound response to the values System B expects,
/// driven by <see cref="ResponseTransformOptions"/>. Uses namespace-agnostic local-name() XPath
/// queries, matching the approach in <see cref="SpiXmlParser"/>.
/// </summary>
public sealed class InboundResponseTransformer : IInboundResponseTransformer
{
    private readonly IReadOnlyList<ResponseTransformRule> _rules;
    private readonly ILogger<InboundResponseTransformer> _logger;

    public InboundResponseTransformer(
        IOptions<ResponseTransformOptions> options,
        ILogger<InboundResponseTransformer> logger)
    {
        _rules = options.Value.Rules ?? ResponseTransformOptions.DefaultRules;
        _logger = logger;
    }

    public string Transform(string responseXml, string? systemAPacs008Xml, string systemBPacs008Xml)
    {
        var responseDoc = new XmlDocument { PreserveWhitespace = true };
        responseDoc.LoadXml(responseXml);

        // System A's request is optional: when absent, each rule's baseline value is read from the
        // response node itself (Bacen echoes System A's identifiers back into the response).
        XmlDocument? sentA = null;
        if (systemAPacs008Xml is not null)
        {
            sentA = new XmlDocument();
            sentA.LoadXml(systemAPacs008Xml);
        }
        var sentB = new XmlDocument();
        sentB.LoadXml(systemBPacs008Xml);

        var mutated = false;
        foreach (var rule in _rules)
        {
            var valueB = SelectText(sentB, rule.SentValueXPath);
            if (valueB is null)
                continue; // nothing to map System B to

            var target = SelectNode(responseDoc, rule.ResponseTargetXPath);
            if (target is null)
            {
                _logger.LogDebug(
                    "Transform rule {Rule}: response target node absent, skipping.", rule.Name);
                continue;
            }

            // Baseline "System A value": prefer System A's stored request; otherwise fall back to the
            // response node's own text, which carries System A's echoed value.
            var valueA = sentA is not null
                ? SelectText(sentA, rule.SentValueXPath)
                : target.InnerText.Trim();

            // Nothing to map if the baseline is missing or the values already match.
            if (valueA is null || string.Equals(valueA, valueB, StringComparison.Ordinal))
                continue;

            target.InnerText = valueB;
            mutated = true;
            _logger.LogDebug(
                "Transform rule {Rule}: rewrote '{From}' -> '{To}'.", rule.Name, valueA, valueB);
        }

        return mutated ? responseDoc.OuterXml : responseXml;
    }

    private static string? SelectText(XmlDocument doc, string xpath) =>
        SelectNode(doc, xpath)?.InnerText.Trim();

    private static XmlNode? SelectNode(XmlDocument doc, string xpath)
    {
        try
        {
            return doc.SelectSingleNode(xpath);
        }
        catch (XPathException)
        {
            return null;
        }
    }
}
