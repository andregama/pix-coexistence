using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace ConvivenciaPix.V2.Infrastructure.Signing;

/// <summary>
/// Software XML-DSig signer used by the non-production HSM paths (dev mock and the local Dinamo
/// SDK simulation). Produces an enveloped signature and inserts it into <c>AppHdr/Sgntr</c>,
/// matching the placement the real Dinamo HSM's <c>SignPIX</c> emits for Bacen SPI messages.
/// It is NOT the SPI signature profile (which has three references, exclusive canonicalization and
/// an issuer/serial KeyInfo): a system that validates signatures against the SPI schema will only
/// accept what the real HSM produces. Works on bytes so that the document's own encoding declaration (and any BOM) is honoured.
/// </summary>
internal static class EnvelopedXmlSigner
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static byte[] Sign(byte[] xml, X509Certificate2 certificate)
    {
        // Messages from Bacen arrive already signed; strip any existing signature so we never emit
        // an envelope with two <Signature> elements.
        var xmlDoc = Load(xml);
        RemoveSignatures(xmlDoc);

        var signedXml = new SignedXml(xmlDoc)
        {
            SigningKey = certificate.GetRSAPrivateKey()
                ?? throw new InvalidOperationException("Certificate does not contain an RSA private key.")
        };

        var reference = new Reference { Uri = "" };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigC14NTransform());
        signedXml.AddReference(reference);

        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(certificate));
        signedXml.KeyInfo = keyInfo;

        signedXml.ComputeSignature();
        var signatureElement = signedXml.GetXml();

        // SPI messages carry the signature inside the BAH's <Sgntr> element. Fall back to the
        // document root only for envelopes that do not declare one.
        var sgntr = xmlDoc.SelectSingleNode("//*[local-name()='AppHdr']/*[local-name()='Sgntr']")
            ?? xmlDoc.SelectSingleNode("//*[local-name()='Sgntr']");
        var imported = xmlDoc.ImportNode(signatureElement, true);
        if (sgntr is not null)
            sgntr.AppendChild(imported);
        else
            xmlDoc.DocumentElement!.AppendChild(imported);

        return Save(xmlDoc);
    }

    /// <summary>
    /// Removes every XML-DSig <c>&lt;Signature&gt;</c> element from the envelope, leaving the
    /// containing <c>&lt;Sgntr&gt;</c> in place so a fresh signature can be inserted. Returns the
    /// input unchanged (no reserialization) when there is nothing to remove.
    /// </summary>
    public static byte[] StripSignatures(byte[] xml)
    {
        var xmlDoc = Load(xml);
        return RemoveSignatures(xmlDoc) ? Save(xmlDoc) : xml;
    }

    public static bool Verify(byte[] xml, X509Certificate2 certificate)
    {
        var xmlDoc = Load(xml);

        var signatureNodes = xmlDoc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl);
        if (signatureNodes.Count == 0)
            return false;

        var signed = new SignedXml(xmlDoc);
        signed.LoadXml((XmlElement)signatureNodes[0]!);
        return signed.CheckSignature(certificate, verifySignatureOnly: true);
    }

    private static XmlDocument Load(byte[] xml)
    {
        var xmlDoc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        using var stream = new MemoryStream(xml, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        xmlDoc.Load(reader);
        return xmlDoc;
    }

    private static bool RemoveSignatures(XmlDocument xmlDoc)
    {
        // Snapshot first: GetElementsByTagName returns a live list that mutates as nodes are removed.
        var signatureNodes = xmlDoc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl).Cast<XmlNode>().ToList();
        foreach (var node in signatureNodes)
        {
            var parent = node.ParentNode;
            parent?.RemoveChild(node);
            // Back to <Sgntr/> rather than <Sgntr></Sgntr>, the form of an unsigned SPI envelope.
            if (parent is XmlElement { HasChildNodes: false } element)
                element.IsEmpty = true;
        }
        return signatureNodes.Count > 0;
    }

    // SPI messages are UTF-8; the declaration (when present) already says so.
    private static byte[] Save(XmlDocument xmlDoc) => Utf8NoBom.GetBytes(xmlDoc.OuterXml);
}
