using ConvivenciaPix.Application.Interfaces;
using System.Xml;
using System.Xml.XPath;

namespace ConvivenciaPix.Infrastructure.Parsing;

/// <summary>
/// Parses ISO 20022 SPI messages (pacs.008, pacs.004) used by Bacen.
/// XPath queries are namespace-aware; missing optional fields return safe defaults.
/// </summary>
public sealed class SpiXmlParser : ISpiXmlParser
{
    // Namespaces used by Bacen SPI messages
    private static readonly (string Prefix, string Uri)[] KnownNamespaces =
    [
        ("pacs008", "urn:iso:std:iso:20022:tech:xsd:pacs.008.001.08"),
        ("pacs004", "urn:iso:std:iso:20022:tech:xsd:pacs.004.001.09"),
        ("head",    "urn:iso:std:iso:20022:tech:xsd:head.001.001.02"),
        ("doc",     "urn:iso:std:iso:20022:tech:xsd:pacs.008.001.08"),
    ];

    public string ExtractMessageId(string xml)
    {
        var (doc, ns) = Load(xml);
        return SelectText(doc, ns,
            "//head:AppHdr/head:MsgId",
            "//*[local-name()='MsgId']")
            ?? throw new InvalidOperationException("SPI XML missing <MsgId>");
    }

    public string ExtractEndToEndId(string xml)
    {
        var (doc, ns) = Load(xml);
        return SelectText(doc, ns,
            "//*[local-name()='EndToEndId']")
            ?? throw new InvalidOperationException("SPI XML missing <EndToEndId>");
    }

    public decimal ExtractAmount(string xml)
    {
        var (doc, ns) = Load(xml);
        var raw = SelectText(doc, ns,
            "//*[local-name()='IntrBkSttlmAmt']",
            "//*[local-name()='InstdAmt']")
            ?? "0";

        // Strip currency attribute value if element has it
        return decimal.TryParse(raw, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var amount)
            ? amount
            : 0m;
    }

    public string ExtractPayerId(string xml)
    {
        var (doc, ns) = Load(xml);
        return SelectText(doc, ns,
            "//*[local-name()='Dbtr']/*[local-name()='Id']/*[local-name()='OrgId']/*[local-name()='AnyBIC']",
            "//*[local-name()='Dbtr']/*[local-name()='Nm']",
            "//*[local-name()='Dbtr']/*[local-name()='Id']")
            ?? string.Empty;
    }

    public string ExtractPayeeId(string xml)
    {
        var (doc, ns) = Load(xml);
        return SelectText(doc, ns,
            "//*[local-name()='Cdtr']/*[local-name()='Id']/*[local-name()='OrgId']/*[local-name()='AnyBIC']",
            "//*[local-name()='Cdtr']/*[local-name()='Nm']",
            "//*[local-name()='Cdtr']/*[local-name()='Id']")
            ?? string.Empty;
    }

    public DateTimeOffset ExtractTimestamp(string xml)
    {
        var (doc, ns) = Load(xml);
        var raw = SelectText(doc, ns,
            "//*[local-name()='IntrBkSttlmDt']",
            "//*[local-name()='CreDtTm']");

        return raw is not null && DateTimeOffset.TryParse(raw, out var ts)
            ? ts
            : DateTimeOffset.UtcNow;
    }

    private static (XmlDocument doc, XmlNamespaceManager ns) Load(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var ns = new XmlNamespaceManager(doc.NameTable);
        foreach (var (prefix, uri) in KnownNamespaces)
            ns.AddNamespace(prefix, uri);
        return (doc, ns);
    }

    private static string? SelectText(XmlDocument doc, XmlNamespaceManager ns, params string[] xpaths)
    {
        foreach (var xpath in xpaths)
        {
            try
            {
                var node = doc.SelectSingleNode(xpath, ns);
                if (node is not null)
                    return node.InnerText.Trim();
            }
            catch (XPathException)
            {
                // Try next xpath
            }
        }
        return null;
    }
}
