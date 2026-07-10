using ConvivenciaPix.Application.Interfaces;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace ConvivenciaPix.Infrastructure.Parsing;

/// <summary>
/// Synthesises a SPI Echo reply (pibr.002 / EchoRpt) from an Echo request (pibr.001 / EchoReq),
/// per Catálogo de Serviços do SFN v5.12.1. Reads the request namespace-agnostically (local-name
/// XPath, matching <see cref="SpiXmlParser"/>) and emits a schema-shaped pibr.002 document.
/// </summary>
public sealed class Pibr002Builder : IPibr002Builder
{
    private const string Pibr002Namespace = "https://www.bcb.gov.br/pi/pibr.002/1.3";
    private const string Pibr002MsgDefIdr = "pibr.002.spi.1.3";

    // MsgIdType = [M][0-9A-Z]{8}[a-zA-Z0-9]{23} (length 32). The 8 chars are the sender ISPB.
    private const string MsgIdAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const int MsgIdRandomLength = 23;

    public string Build(string pibr001Xml)
    {
        var req = new XmlDocument();
        req.LoadXml(pibr001Xml);

        // Fr/To are the PSP ISPBs; the reply is sent by the original recipient back to the sender.
        var requesterIspb = Require(req, "//*[local-name()='AppHdr']/*[local-name()='Fr']//*[local-name()='Id']", "AppHdr/Fr Id");
        var responderIspb = Require(req, "//*[local-name()='AppHdr']/*[local-name()='To']//*[local-name()='Id']", "AppHdr/To Id");
        var echoData = Require(req, "//*[local-name()='EchoReq']/*[local-name()='EchoTxInf']/*[local-name()='Data']", "EchoReq/EchoTxInf/Data");

        var msgId = GenerateMsgId(responderIspb);
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        XNamespace ns = Pibr002Namespace;
        var envelope = new XElement(ns + "Envelope",
            new XElement(ns + "AppHdr",
                Party(ns, "Fr", responderIspb),
                Party(ns, "To", requesterIspb),
                new XElement(ns + "BizMsgIdr", msgId),
                new XElement(ns + "MsgDefIdr", Pibr002MsgDefIdr),
                new XElement(ns + "CreDt", now),
                new XElement(ns + "Sgntr")),
            new XElement(ns + "Document",
                new XElement(ns + "EchoRpt",
                    new XElement(ns + "GrpHdr",
                        new XElement(ns + "MsgId", msgId),
                        new XElement(ns + "CreDtTm", now)),
                    new XElement(ns + "EchoTxInf",
                        new XElement(ns + "OrgnlData", echoData)))));

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", "no"), envelope);
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement Party(XNamespace ns, string role, string ispb) =>
        new(ns + role,
            new XElement(ns + "FIId",
                new XElement(ns + "FinInstnId",
                    new XElement(ns + "Othr",
                        new XElement(ns + "Id", ispb)))));

    private static string GenerateMsgId(string senderIspb)
    {
        Span<char> random = stackalloc char[MsgIdRandomLength];
        for (var i = 0; i < random.Length; i++)
            random[i] = MsgIdAlphabet[RandomNumberGenerator.GetInt32(MsgIdAlphabet.Length)];
        return $"M{senderIspb}{new string(random)}";
    }

    private static string Require(XmlDocument doc, string xpath, string field)
    {
        var value = doc.SelectSingleNode(xpath)?.InnerText.Trim();
        return string.IsNullOrEmpty(value)
            ? throw new InvalidOperationException($"pibr.001 XML missing {field}")
            : value;
    }
}
