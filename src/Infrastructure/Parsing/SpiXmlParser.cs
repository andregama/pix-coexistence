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

    // All SPI message types this parser recognises, matched against AppHdr/MsgDefIdr by "<family>.<nn>"
    // prefix. Order does not matter — the prefixes are mutually exclusive.
    private static readonly string[] KnownMessageTypes =
    [
        "pacs.008", "pacs.002", "pacs.004", "pibr.001", "pibr.002",
        "pain.009", "pain.011", "pain.012", "pain.013", "pain.014",
        "camt.055", "camt.029", "camt.025", "camt.014",
        "reda.041", "admi.002", "admi.004",
    ];

    // Types whose correlation key is derived from the recurrenceId (IdRec), because the orchestrator
    // cannot align the message-level idempotency keys System A and System B mint independently.
    // (pain.009/pain.013 are excluded: System A only receives them, so they carry Bacen-assigned keys
    // already shared across both systems.)
    private static readonly HashSet<string> RecurrenceDerivedKeyTypes =
        new(StringComparer.OrdinalIgnoreCase) { "pain.011", "pain.012", "pain.014" };

    // Types whose correlation key is the original payment's OrgnlEndToEndId — shared because it
    // references the already-correlated pacs.008.
    private static readonly HashSet<string> OriginalPaymentKeyTypes =
        new(StringComparer.OrdinalIgnoreCase) { "camt.055", "camt.029" };

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
            // The MsgDefIdr always starts with the "<family>.<number>" (e.g. "pain.012.001.08"),
            // so a prefix match on the first eight chars uniquely identifies the type.
            foreach (var type in KnownMessageTypes)
                if (msgDefIdr.StartsWith(type, StringComparison.OrdinalIgnoreCase))
                    return type;
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
            "FIToFICstmrCdtTrf"       => "pacs.008",
            "FIToFIPmtStsRpt"         => "pacs.002",
            "PmtRtr"                  => "pacs.004",
            "EchoReq"                 => "pibr.001",
            "EchoRpt"                 => "pibr.002",
            // Pix Automático (recurring authorization) and its status reports.
            "MndtInitnReq"            => "pain.009",
            "MndtCxlReq"              => "pain.011",
            "MndtAccptncRpt"          => "pain.012",
            "CdtrPmtActvtnReq"        => "pain.013",
            "CdtrPmtActvtnReqStsRpt"  => "pain.014",
            // Cancellation / investigation.
            "CstmrPmtCxlReq"          => "camt.055",
            "RsltnOfInvstgtn"         => "camt.029",
            "Rct"                     => "camt.025",
            "GetGnlBizInf"            => "camt.014",
            // Reference data (Pix Automático party/authorization) and administrative.
            "PtyAndAcctInf"           => "reda.041",
            "MErr" or "MssgRjctn"     => "admi.002",
            "SysEvtNtfctn"            => "admi.004",
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

            // Pix Automático / cancellation / administrative families: the message-level idempotency
            // key is the header MsgId (AppHdr BizMsgIdr, falling back to GrpHdr/MsgId).
            _ when KnownMessageTypes.Contains(msgType) => ExtractHeaderMsgId(doc, ns)
                ?? throw new InvalidOperationException($"{msgType} XML missing AppHdr/GrpHdr MsgId"),

            _ => throw new ArgumentException($"Unsupported msgType '{msgType}'", nameof(msgType))
        };
    }

    public string ExtractCorrelationKey(string xml, string msgType)
    {
        if (RecurrenceDerivedKeyTypes.Contains(msgType))
        {
            // recurrenceId (IdRec) is shared across A and B; msgType keeps distinct message types on
            // the same recurrence from colliding on one row. NOTE: if two messages of the same type
            // can occur for one recurrenceId within the TTL window, fold in the per-exchange
            // discriminator the Catálogo de Serviços do SFN defines (confirm the field).
            var recurrenceId = ExtractRecurrenceId(xml)
                ?? throw new InvalidOperationException(
                    $"{msgType} XML missing recurrenceId (IdRec); cannot derive correlation key");
            return $"{msgType}:{recurrenceId}";
        }

        if (OriginalPaymentKeyTypes.Contains(msgType))
        {
            var (doc, ns) = Load(xml);
            return SelectText(doc, ns, "//*[local-name()='OrgnlEndToEndId']")
                ?? throw new InvalidOperationException(
                    $"{msgType} XML missing OrgnlEndToEndId; cannot derive correlation key");
        }

        // All other types correlate on the shared message-level idempotency key.
        return ExtractIdempotentId(xml, msgType);
    }

    public string GetCorrelationSource(string msgType) =>
        RecurrenceDerivedKeyTypes.Contains(msgType) || OriginalPaymentKeyTypes.Contains(msgType)
            ? "DerivedKey"
            : "MessageKey";

    public string? ExtractOriginalIdempotentId(string xml, string msgType)
    {
        var (doc, ns) = Load(xml);
        return msgType switch
        {
            // Return of a payment: back-link to the original pacs.008 EndToEndId.
            "pacs.004" => SelectText(doc, ns, "//*[local-name()='TxInf']/*[local-name()='OrgnlEndToEndId']"),

            // Status reports / rejections carry the answered message's MsgId in OrgnlMsgId; the
            // Correlate Worker uses this to rewrite A's reference to B's before delivery.
            "pain.012" or "pain.014" or "camt.025" or "camt.029" or "admi.002" => SelectText(doc, ns,
                "//*[local-name()='OrgnlMsgId']",
                "//*[local-name()='OrgnlMsgNmId']",
                "//*[local-name()='RltdRef']/*[local-name()='Ref']"),

            _ => null
        };
    }

    /// <summary>
    /// recurrenceId (IdRec) — the Pix Automático authorization identifier shared by System A and B.
    /// Tries the known Bacen/ISO carriers; exact path to confirm against the Catálogo de Serviços.
    /// </summary>
    private static string? ExtractRecurrenceId(string xml)
    {
        var (doc, ns) = Load(xml);
        return SelectText(doc, ns,
            "//*[local-name()='IdRec']",
            "//*[local-name()='RcrrgId']",
            "//*[local-name()='Rcrr']/*[local-name()='Id']",
            "//*[local-name()='MndtId']");
    }

    private static string? ExtractHeaderMsgId(XmlDocument doc, XmlNamespaceManager ns) =>
        SelectText(doc, ns,
            "//head:AppHdr/head:MsgId",
            "//*[local-name()='AppHdr']/*[local-name()='MsgId']",
            "//*[local-name()='AppHdr']/*[local-name()='BizMsgIdr']",
            "//*[local-name()='GrpHdr']/*[local-name()='MsgId']");

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
