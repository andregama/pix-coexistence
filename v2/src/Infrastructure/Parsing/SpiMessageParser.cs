using ConvivenciaPix.V2.Application.Parsing;
using ConvivenciaPix.V2.Domain.Routing;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ConvivenciaPix.V2.Infrastructure.Parsing;

/// <summary>
/// Extracts, in a single pass, what routing needs from an ISO 20022 / Bacen SPI message. Lookups are
/// by element local name (namespace-agnostic) and independent of the message type wherever the
/// element name is unambiguous, so new message versions keep working without code changes.
/// </summary>
public sealed partial class SpiMessageParser : ISpiMessageParser
{
    private const int MaxKeys = 32;

    // "<4 letters>.<3 digits>" (e.g. "pacs.002"), as found in MsgDefIdr ("pacs.002.spi.1.17") or
    // the message namespace ("https://www.bcb.gov.br/pi/pacs.002/1.17").
    [GeneratedRegex(@"\b([a-z]{4}\.\d{3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex FamilyTokenRegex();

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true
    };

    public ParsedSpiMessage Parse(ReadOnlyMemory<byte> xml)
    {
        XElement root;
        try
        {
            using var stream = new MemoryStream(xml.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, ReaderSettings);
            root = XDocument.Load(reader).Root!;
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException)
        {
            return ParsedSpiMessage.Unparseable;
        }

        if (root is null)
            return ParsedSpiMessage.Unparseable;

        var recipient = Values(root, "AppHdr", "To", "FIId", "FinInstnId", "Othr", "Id").FirstOrDefault();

        return new ParsedSpiMessage(
            DetectType(root) ?? ParsedSpiMessage.UnknownType,
            MessageId(root),
            OriginalMessageRefs(root),
            // A message without transaction containers is one indivisible unit.
            (SpiTransactionSegments.Find(root) ?? [SpiTransactionSegments.Document(root)])
                .Select(segment => Transaction(segment, recipient))
                .ToList(),
            EchoData(root));
    }

    private static ParsedTransaction Transaction(XElement segment, string? recipient)
    {
        var creditor = PartyDocument(segment, "Cdtr");
        var debtor = PartyDocument(segment, "Dbtr");

        // The bank the message is addressed to is one of the two agents; its customer is ours.
        var creditorAgent = Values(segment, "CdtrAgt", "FinInstnId", "ClrSysMmbId", "MmbId").FirstOrDefault();
        var debtorAgent = Values(segment, "DbtrAgt", "FinInstnId", "ClrSysMmbId", "MmbId").FirstOrDefault();
        string? ownSide = null;
        if (recipient is not null && creditorAgent != debtorAgent)
            ownSide = recipient == creditorAgent ? creditor : recipient == debtorAgent ? debtor : null;

        return new ParsedTransaction(TransactionKeys(segment), creditor, debtor, ownSide);
    }

    private static string? DetectType(XElement root)
    {
        // 1. Business Application Header: "<family>.<nnn>.spi.<v>" (SPI) or "<family>.<nnn>.001.<vv>" (ISO).
        var fromHeader = MatchFamilyToken(Values(root, "MsgDefIdr").FirstOrDefault());
        if (fromHeader is not null)
            return fromHeader;

        // 2. Namespace of the business message (child of <Document>, or the root itself).
        var business = root.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == "Document")?.Elements().FirstOrDefault()
            ?? root;
        var fromNamespace = MatchFamilyToken(business.Name.NamespaceName);
        if (fromNamespace is not null)
            return fromNamespace;

        // 3. Local name of the business message element.
        return business.Name.LocalName switch
        {
            "FIToFICstmrCdtTrf" => "pacs.008",
            "FIToFIPmtStsRpt" => "pacs.002",
            "PmtRtr" => "pacs.004",
            "EchoReq" => "pibr.001",
            "EchoRpt" => "pibr.002",
            "MndtInitnReq" => "pain.009",
            "MndtCxlReq" => "pain.011",
            "MndtAccptncRpt" => "pain.012",
            "CdtrPmtActvtnReq" => "pain.013",
            "CdtrPmtActvtnReqStsRpt" => "pain.014",
            "CstmrPmtCxlReq" => "camt.055",
            "RsltnOfInvstgtn" => "camt.029",
            "Rct" => "camt.025",
            "GetGnlBizInf" => "camt.014",
            "PtyAndAcctInf" => "reda.041",
            "MErr" or "MssgRjctn" => "admi.002",
            "SysEvtNtfctn" => "admi.004",
            _ => null
        };
    }

    private static string? MatchFamilyToken(string? source)
    {
        if (string.IsNullOrEmpty(source)) return null;
        var match = FamilyTokenRegex().Match(source);
        return match.Success ? match.Groups[1].Value : null;
    }

    // SPI identifies the message in AppHdr/BizMsgIdr; ISO head.001 uses AppHdr/MsgId; pacs/pain
    // repeat it in GrpHdr/MsgId.
    private static string? MessageId(XElement root) =>
        Values(root, "AppHdr", "BizMsgIdr").FirstOrDefault()
        ?? Values(root, "AppHdr", "MsgId").FirstOrDefault()
        ?? Values(root, "GrpHdr", "MsgId").FirstOrDefault();

    // Every place a message can name the message it answers. Placeholders (all zeros) are ignored.
    private static List<string> OriginalMessageRefs(XElement root)
    {
        var refs = new List<string>();

        foreach (var element in root.Descendants().Where(e => e.Name.LocalName == "OrgnlMsgId"))
        {
            // Either a leaf (<OrgnlMsgId>id</OrgnlMsgId>) or a wrapper (<OrgnlMsgId><MsgId>id</MsgId></OrgnlMsgId>).
            if (element.HasElements)
                AddAll(refs, element.Elements().Where(e => e.Name.LocalName == "MsgId").Select(Text));
            else
                AddAll(refs, [Text(element)]);
        }

        AddAll(refs, Values(root, "OrgnlMsgInf", "MsgId"));
        AddAll(refs, Values(root, "OrgnlBizQry", "MsgId"));
        // reda.016 answering a reda.014/022/031.
        AddAll(refs, Values(root, "OrgnlBizInstr", "MsgId"));
        // admi.002: the rejected message's id, or its PI-ResourceId when the id could not be read.
        AddAll(refs, Values(root, "RltdRef", "Ref"));
        // camt.052/053/054 answering a camt.060 carry its MsgId as the report id (zeros when unsolicited).
        AddAll(refs, Values(root, "Rpt", "Id"));
        AddAll(refs, Values(root, "Stmt", "Id"));
        AddAll(refs, Values(root, "Ntfctn", "Id"));

        return refs;
    }

    private static List<TxKey> TransactionKeys(XElement root)
    {
        var keys = new List<TxKey>();

        void Add(TxKeyKind kind, IEnumerable<string?> values)
        {
            foreach (var value in values)
            {
                if (keys.Count >= MaxKeys) return;
                if (IsPlaceholder(value)) continue;
                var key = new TxKey(kind, value!);
                if (!keys.Contains(key))
                    keys.Add(key);
            }
        }

        // The instruction a status report is about comes first: it is the most specific reference
        // (for the status of a return it is the RtrId, while OrgnlEndToEndId is the original payment).
        AddPaymentIds(Values(root, "OrgnlInstrId"));
        AddPaymentIds(Values(root, "OrgnlEndToEndId"));
        AddPaymentIds(Values(root, "EndToEndId"));
        AddPaymentIds(Values(root, "OrgnlPmtId", "PrtryId"));
        Add(TxKeyKind.RTR, Values(root, "RtrId"));
        Add(TxKeyKind.REC, Values(root, "MndtId"));

        // SPI ids say what they identify: "E…" is a payment (EndToEndId), "D…" a return (RtrId).
        void AddPaymentIds(IEnumerable<string> values)
        {
            foreach (var value in values)
                Add(value.StartsWith('D') ? TxKeyKind.RTR : TxKeyKind.E2E, [value]);
        }

        return keys;
    }

    // CPF/CNPJ of a party: <Cdtr|Dbtr><Id><PrvtId|OrgId><Othr><Id>.
    private static string? PartyDocument(XElement root, string party)
    {
        var document = Values(root, party, "Id", "PrvtId", "Othr", "Id").FirstOrDefault()
            ?? Values(root, party, "Id", "OrgId", "Othr", "Id").FirstOrDefault();
        var normalized = CustomerDocument.Normalize(document);
        return normalized.Length == 0 ? null : normalized;
    }

    // pibr.001 carries the content in EchoTxInf/Data; pibr.002 returns it in EchoTxInf/OrgnlData.
    private static string? EchoData(XElement root) =>
        Values(root, "EchoTxInf", "Data").FirstOrDefault()
        ?? Values(root, "EchoTxInf", "OrgnlData").FirstOrDefault();

    /// <summary>
    /// Non-empty text of every element reached by <paramref name="path"/>: a descendant named
    /// path[0] at any depth, then direct children for each following segment.
    /// </summary>
    private static IEnumerable<string> Values(XElement root, params string[] path)
    {
        IEnumerable<XElement> current = root.Descendants().Where(e => e.Name.LocalName == path[0]);
        for (var i = 1; i < path.Length; i++)
        {
            var segment = path[i];
            current = current.Elements().Where(e => e.Name.LocalName == segment);
        }
        return current.Where(e => !e.HasElements).Select(Text).Where(v => v.Length > 0);
    }

    private static string Text(XElement element) => element.Value.Trim();

    private static void AddAll(List<string> target, IEnumerable<string?> values)
    {
        foreach (var value in values)
        {
            if (target.Count >= MaxKeys) return;
            if (!IsPlaceholder(value) && !target.Contains(value!))
                target.Add(value!);
        }
    }

    private static bool IsPlaceholder(string? value) =>
        string.IsNullOrEmpty(value)
        || value.Length > 64
        || value.All(c => c == '0')
        || value.Equals("NOTPROVIDED", StringComparison.OrdinalIgnoreCase);
}
