using ConvivenciaPix.Application.Interfaces;
using System.Xml;
using System.Xml.XPath;

namespace ConvivenciaPix.Infrastructure.Parsing;

/// <summary>
/// Parses ISO 20022 SPI messages (pacs.008, pacs.002, pacs.004) used by Bacen.
/// XPath queries are namespace-aware; missing optional fields return safe defaults.
/// </summary>
public sealed class SpiXmlParser : ISpiXmlParser
{
    private static readonly (string Prefix, string Uri)[] KnownNamespaces =
    [
        ("pacs008", "urn:iso:std:iso:20022:tech:xsd:pacs.008.001.08"),
        ("pacs002", "urn:iso:std:iso:20022:tech:xsd:pacs.002.001.10"),
        ("pacs004", "urn:iso:std:iso:20022:tech:xsd:pacs.004.001.09"),
        ("head",    "urn:iso:std:iso:20022:tech:xsd:head.001.001.02"),
    ];

    public string ExtractMessageId(string xml)
    {
        var (doc, ns) = Load(xml);
        return SelectText(doc, ns,
            "//head:AppHdr/head:MsgId",
            "//*[local-name()='MsgId']")
            ?? throw new InvalidOperationException("SPI XML missing <MsgId>");
    }

    public decimal ExtractAmount(string xml)
    {
        var (doc, ns) = Load(xml);
        var raw = SelectText(doc, ns,
            "//*[local-name()='IntrBkSttlmAmt']",
            "//*[local-name()='InstdAmt']")
            ?? "0";

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

    public string ExtractMessageType(string xml)
    {
        var (doc, ns) = Load(xml);

        // ISO 20022 business application header carries <MsgDefIdr> with the message type
        var msgDefIdr = SelectText(doc, ns,
            "//head:AppHdr/head:MsgDefIdr",
            "//*[local-name()='MsgDefIdr']");

        if (msgDefIdr is not null)
        {
            if (msgDefIdr.StartsWith("pacs.008", StringComparison.OrdinalIgnoreCase)) return "pacs.008";
            if (msgDefIdr.StartsWith("pacs.002", StringComparison.OrdinalIgnoreCase)) return "pacs.002";
            if (msgDefIdr.StartsWith("pacs.004", StringComparison.OrdinalIgnoreCase)) return "pacs.004";
            if (msgDefIdr.StartsWith("pibr.001", StringComparison.OrdinalIgnoreCase)) return "pibr.001";
            if (msgDefIdr.StartsWith("pibr.002", StringComparison.OrdinalIgnoreCase)) return "pibr.002";
        }

        // Fallback: infer from the business message element name.
        // Real Bacen XML wraps the business message in <Document>. For pacs.* the root is
        // "Document" itself; for the SPI Echo (pibr) the root is "Envelope" and <Document> is a
        // child, so in both cases we look at <Document>'s first business child.
        var rootName = doc.DocumentElement?.LocalName;
        var businessElement = rootName switch
        {
            "Document" => doc.DocumentElement?.FirstChild?.LocalName,
            "Envelope" => doc.SelectSingleNode("//*[local-name()='Document']/*", ns)?.LocalName,
            _ => rootName
        };

        return businessElement switch
        {
            "FIToFICstmrCdtTrf" => "pacs.008",
            "FIToFIPmtStsRpt"   => "pacs.002",
            "PmtRtr"            => "pacs.004",
            "EchoReq"           => "pibr.001",
            "EchoRpt"           => "pibr.002",
            _ => throw new InvalidOperationException(
                $"Cannot determine message type from business element '{businessElement}' and no MsgDefIdr found.")
        };
    }

    public string ExtractIdempotentId(string xml, string msgType)
    {
        var (doc, ns) = Load(xml);
        return msgType switch
        {
            "pacs.008" => SelectText(doc, ns,
                    "//*[local-name()='CdtTrfTxInf']/*[local-name()='PmtId']/*[local-name()='EndToEndId']")
                ?? throw new InvalidOperationException("pacs.008 XML missing CdtTrfTxInf/PmtId/EndToEndId"),

            "pacs.002" => SelectText(doc, ns,
                    "//*[local-name()='TxInfAndSts']/*[local-name()='OrgnlEndToEndId']")
                ?? throw new InvalidOperationException("pacs.002 XML missing TxInfAndSts/OrgnlEndToEndId"),

            "pacs.004" => SelectText(doc, ns,
                    "//*[local-name()='TxInf']/*[local-name()='RtrId']")
                ?? throw new InvalidOperationException("pacs.004 XML missing TxInf/RtrId"),

            // SPI Echo (pibr.001) has no EndToEndId; the message identity is GrpHdr/MsgId
            // (equal to AppHdr/BizMsgIdr).
            "pibr.001" => SelectText(doc, ns,
                    "//*[local-name()='EchoReq']/*[local-name()='GrpHdr']/*[local-name()='MsgId']")
                ?? throw new InvalidOperationException("pibr.001 XML missing EchoReq/GrpHdr/MsgId"),

            _ => throw new ArgumentException($"Unsupported msgType '{msgType}'", nameof(msgType))
        };
    }

    public string? ExtractOriginalIdempotentId(string xml, string msgType)
    {
        if (msgType != "pacs.004")
            return null;

        var (doc, ns) = Load(xml);
        return SelectText(doc, ns,
            "//*[local-name()='TxInf']/*[local-name()='OrgnlEndToEndId']");
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
                // try next xpath
            }
        }
        return null;
    }
}
