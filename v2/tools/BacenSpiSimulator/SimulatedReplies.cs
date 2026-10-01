using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace ConvivenciaPix.V2.BacenSpiSimulator;

/// <summary>
/// The replies the SPI would send for the messages a participant posts: a pacs.002 for a pacs.008
/// and a pibr.002 for a pibr.001. Shapes follow the SPI catalog envelope (AppHdr + Document).
/// </summary>
public static class SimulatedReplies
{
    private const string BacenIspb = "00038166";
    private const string MsgIdAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    /// <summary>Marker a test can put in a pacs.008 to have it rejected (RJCT) instead of accepted.</summary>
    public const string RejectMarker = "SIM-REJECT";

    public static (string MsgType, string? MsgId) Identify(byte[] xml)
    {
        try
        {
            var root = XDocument.Parse(Encoding.UTF8.GetString(xml)).Root!;
            var definition = First(root, "MsgDefIdr") ?? root.Name.NamespaceName;
            var match = System.Text.RegularExpressions.Regex.Match(definition, @"[a-z]{4}\.\d{3}");
            return (match.Success ? match.Value : "unknown", First(root, "BizMsgIdr"));
        }
        catch (System.Xml.XmlException)
        {
            return ("unknown", null);
        }
    }

    /// <summary>Returns the reply the SPI would produce for the message, or null when it has none.</summary>
    public static byte[]? For(byte[] xml)
    {
        XElement root;
        try
        {
            root = XDocument.Parse(Encoding.UTF8.GetString(xml)).Root!;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var (msgType, _) = Identify(xml);
        var participant = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fr")?
            .Descendants().FirstOrDefault(e => e.Name.LocalName == "Id")?.Value.Trim() ?? "00000000";

        return msgType switch
        {
            "pacs.008" when First(root, "EndToEndId") is { } endToEndId =>
                Pacs002(participant, endToEndId, root.ToString().Contains(RejectMarker) ? "RJCT" : "ACSP"),
            "pibr.001" when First(root, "Data") is { } data => Pibr002(participant, data),
            _ => null
        };
    }

    // As in the SPI catalog, a pacs.002 names the instruction it reports on, not a message.
    public static byte[] Pacs002(string participantIspb, string originalEndToEndId, string status)
    {
        XNamespace ns = "https://www.bcb.gov.br/pi/pacs.002/1.17";
        var msgId = NewMsgId();
        return Envelope(ns, participantIspb, msgId, "pacs.002.spi.1.17",
            new XElement(ns + "FIToFIPmtStsRpt",
                new XElement(ns + "GrpHdr",
                    new XElement(ns + "MsgId", msgId),
                    new XElement(ns + "CreDtTm", Now())),
                new XElement(ns + "TxInfAndSts",
                    new XElement(ns + "OrgnlInstrId", originalEndToEndId),
                    new XElement(ns + "OrgnlEndToEndId", originalEndToEndId),
                    new XElement(ns + "TxSts", status))));
    }

    public static byte[] Pibr002(string participantIspb, string echoData)
    {
        XNamespace ns = "https://www.bcb.gov.br/pi/pibr.002/1.3";
        var msgId = NewMsgId();
        return Envelope(ns, participantIspb, msgId, "pibr.002.spi.1.3",
            new XElement(ns + "EchoRpt",
                new XElement(ns + "GrpHdr",
                    new XElement(ns + "MsgId", msgId),
                    new XElement(ns + "CreDtTm", Now())),
                new XElement(ns + "EchoTxInf",
                    new XElement(ns + "OrgnlData", echoData))));
    }

    private static byte[] Envelope(XNamespace ns, string toIspb, string msgId, string msgDefIdr, XElement business)
    {
        var envelope = new XElement(ns + "Envelope",
            new XElement(ns + "AppHdr",
                Party(ns, "Fr", BacenIspb),
                Party(ns, "To", toIspb),
                new XElement(ns + "BizMsgIdr", msgId),
                new XElement(ns + "MsgDefIdr", msgDefIdr),
                new XElement(ns + "CreDt", Now()),
                new XElement(ns + "Sgntr")),
            new XElement(ns + "Document", business));

        var document = new XDocument(new XDeclaration("1.0", "UTF-8", "no"), envelope);
        return Encoding.UTF8.GetBytes(document.Declaration + document.ToString(SaveOptions.DisableFormatting));
    }

    private static XElement Party(XNamespace ns, string role, string ispb) =>
        new(ns + role,
            new XElement(ns + "FIId",
                new XElement(ns + "FinInstnId",
                    new XElement(ns + "Othr",
                        new XElement(ns + "Id", ispb)))));

    private static string? First(XElement root, string localName) =>
        root.Descendants().FirstOrDefault(e => e.Name.LocalName == localName && !e.HasElements)?.Value.Trim();

    private static string NewMsgId()
    {
        Span<char> random = stackalloc char[23];
        for (var i = 0; i < random.Length; i++)
            random[i] = MsgIdAlphabet[RandomNumberGenerator.GetInt32(MsgIdAlphabet.Length)];
        return $"M{BacenIspb}{new string(random)}";
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
}
