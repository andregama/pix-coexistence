using ConvivenciaPix.Application.Interfaces;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.XPath;

namespace ConvivenciaPix.Infrastructure.Parsing;

/// <summary>
/// Parses ISO 20022 / Bacen SPI messages. Message-type detection is catalog-driven (any
/// "&lt;family&gt;.&lt;nnn&gt;" that appears in the AppHdr MsgDefIdr or the message namespace),
/// so it recognises every family in the SPI catalog, not just a fixed allow-list.
/// XPath queries are namespace-agnostic (local-name based); missing optional fields return safe defaults.
/// </summary>
public sealed partial class SpiXmlParser : ISpiXmlParser
{
    // Matches an ISO/SPI message-family token "<4 letters>.<3 digits>" (e.g. "pacs.002"),
    // as it appears in MsgDefIdr ("pacs.002.spi.1.17" / "pacs.002.001.10") or the message
    // namespace URI ("https://www.bcb.gov.br/pi/pacs.002/1.17").
    [GeneratedRegex(@"\b([a-z]{4}\.\d{3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex FamilyTokenRegex();

    private static string? MatchFamilyToken(string? source)
    {
        if (string.IsNullOrEmpty(source)) return null;
        var m = FamilyTokenRegex().Match(source);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static readonly (string Prefix, string Uri)[] KnownNamespaces =
    [
        ("pacs008", "urn:iso:std:iso:20022:tech:xsd:pacs.008.001.08"),
        ("pacs002", "urn:iso:std:iso:20022:tech:xsd:pacs.002.001.10"),
        ("pacs004", "urn:iso:std:iso:20022:tech:xsd:pacs.004.001.09"),
        ("head",    "urn:iso:std:iso:20022:tech:xsd:head.001.001.02"),
    ];

    // Types whose correlation key is derived from the mandate/recurrence id (Pix Automático
    // authorization), because the orchestrator cannot align the message-level idempotency keys
    // System A and System B mint independently. Per the SPI catalog these carry a mandate id
    // (MndtId) rather than an OrgnlEndToEndId.
    // (pain.009/pain.013 are excluded: System A only receives them, so they carry Bacen-assigned keys
    // already shared across both systems.)
    private static readonly HashSet<string> RecurrenceDerivedKeyTypes =
        new(StringComparer.OrdinalIgnoreCase) { "pain.011", "pain.012" };

    // Types whose correlation key is the original payment's OrgnlEndToEndId — shared because it
    // references the already-correlated pacs.008/pain.013. pain.014 (CdtrPmtActvtnReqStsRpt) belongs
    // here: the SPI catalog example carries OrgnlEndToEndId, not a recurrence id.
    private static readonly HashSet<string> OriginalPaymentKeyTypes =
        new(StringComparer.OrdinalIgnoreCase) { "camt.055", "camt.029", "pain.014" };

    public string ExtractMessageId(string xml)
    {
        var (doc, ns) = Load(xml);
        // The SPI Business Application Header identifies the message via <BizMsgIdr>; the ISO head.001
        // AppHdr uses <MsgId>. pacs/pain also repeat it as <GrpHdr><MsgId>, but admi/camt.029/camt.055
        // have no GrpHdr/MsgId, so BizMsgIdr must be tried first.
        return SelectText(doc, ns,
            "//*[local-name()='AppHdr']/*[local-name()='BizMsgIdr']",
            "//*[local-name()='AppHdr']/*[local-name()='MsgId']",
            "//*[local-name()='GrpHdr']/*[local-name()='MsgId']",
            "//*[local-name()='MsgId']")
            ?? throw new InvalidOperationException("SPI XML missing message id (AppHdr/BizMsgIdr or GrpHdr/MsgId)");
    }

    public decimal ExtractAmount(string xml)
    {
        var (doc, ns) = Load(xml);
        // pacs.008 → IntrBkSttlmAmt; pacs.004 (payment return) → RtrdIntrBkSttlmAmt; instructed amount fallback.
        var raw = SelectText(doc, ns,
            "//*[local-name()='IntrBkSttlmAmt']",
            "//*[local-name()='RtrdIntrBkSttlmAmt']",
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

        // 1. Preferred: the Business Application Header MsgDefIdr. SPI uses "<family>.<nnn>.spi.<v>"
        //    (e.g. "pacs.002.spi.1.17"); ISO uses "<family>.<nnn>.001.<vv>". Both begin with the
        //    "<family>.<nnn>" token, so a token match identifies every catalog family generically.
        var fromMsgDefIdr = MatchFamilyToken(SelectText(doc, ns, "//*[local-name()='MsgDefIdr']"));
        if (fromMsgDefIdr is not null)
            return fromMsgDefIdr;

        // 2. Derive from the business message namespace URI. SPI wraps the message in an <Envelope>
        //    whose (and whose <Document> child's) namespace is "https://www.bcb.gov.br/pi/<family>.<nnn>/<v>";
        //    a bare ISO <Document> carries "urn:iso:std:iso:20022:tech:xsd:<family>.<nnn>.001.<vv>".
        var businessNode = doc.SelectSingleNode("//*[local-name()='Document']/*", ns) ?? doc.DocumentElement;
        var fromNamespace = MatchFamilyToken(businessNode?.NamespaceURI);
        if (fromNamespace is not null)
            return fromNamespace;

        // 3. Last resort: map the business message element's local name.
        var businessElement = businessNode?.LocalName;
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
                $"Cannot determine message type: no MsgDefIdr, no recognised message namespace, " +
                $"and unknown business element '{businessElement}'.")
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

            // trck.002 (internal-transaction status tracker) — the shared key is the internal
            // transaction's EndToEndId, aligned across System A and B (serves dedup + correlation).
            "trck.002" => SelectText(doc, ns,
                    "//*[local-name()='Tx']/*[local-name()='PmtId']/*[local-name()='EndToEndId']")
                ?? throw new InvalidOperationException("trck.002 XML missing Tx/PmtId/EndToEndId"),

            // SPI Echo (pibr.001) has no EndToEndId; the message identity is GrpHdr/MsgId
            // (equal to AppHdr/BizMsgIdr).
            "pibr.001" => SelectText(doc, ns,
                    "//*[local-name()='EchoReq']/*[local-name()='GrpHdr']/*[local-name()='MsgId']")
                ?? throw new InvalidOperationException("pibr.001 XML missing EchoReq/GrpHdr/MsgId"),

            // Every other family (Pix Automático / cancellation / administrative / reference data):
            // the message-level idempotency key is the header id — AppHdr BizMsgIdr, falling back to
            // GrpHdr/MsgId.
            _ => ExtractHeaderMsgId(doc, ns)
                ?? throw new InvalidOperationException($"{msgType} XML missing AppHdr/GrpHdr message id"),
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

        if (msgType == "camt.025")
        {
            // camt.025 (Rct) is SPI/GRAF's receipt for a trck.002; it correlates to the trck.002 pair
            // on the referenced internal-transaction EndToEndId (RctDtls/OrgnlPmtId/PrtryId), which is
            // shared across A and B — analogous to pacs.002 correlating on OrgnlEndToEndId. This is the
            // correlation key only; the camt.025's own idempotency key stays its header id.
            var (doc, ns) = Load(xml);
            return SelectText(doc, ns, "//*[local-name()='OrgnlPmtId']/*[local-name()='PrtryId']")
                ?? throw new InvalidOperationException(
                    "camt.025 XML missing RctDtls/OrgnlPmtId/PrtryId; cannot derive correlation key");
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

            // pain.014 (payment-activation status report) back-links to the original pain.013 payment
            // via OrgnlEndToEndId — its OrgnlMsgId is a zeros placeholder in the SPI catalog.
            "pain.014" => SelectText(doc, ns, "//*[local-name()='OrgnlEndToEndId']"),

            // Status reports / rejections carry the answered message's MsgId in OrgnlMsgId; the
            // Correlate Worker uses this to rewrite A's reference to B's before delivery.
            "pain.012" or "camt.025" or "camt.029" or "admi.002" => SelectText(doc, ns,
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
