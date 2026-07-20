using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using System.Linq;

namespace ConvivenciaPix.Infrastructure.Signing;

/// <summary>
/// Software XML-DSig signer used by the non-production HSM paths (dev mock and the local Dinamo
/// SDK simulation). Produces an enveloped signature and inserts it into <c>AppHdr/Sgntr</c>,
/// matching the placement the real Dinamo HSM's <c>SignPIX</c> emits for Bacen SPI messages.
/// This replaces the former IXmlSigningService, whose only behavioural difference was appending
/// the &lt;Signature&gt; at the document root.
/// </summary>
internal static class EnvelopedXmlSigner
{
    public static string Sign(string unsignedXml, X509Certificate2 certificate)
    {
        // Bacen responses arrive already signed; strip any existing signature so we never emit an
        // envelope with two <Signature> elements (see StripSignatures).
        unsignedXml = StripSignatures(unsignedXml);

        var xmlDoc = new XmlDocument { PreserveWhitespace = true };
        xmlDoc.LoadXml(unsignedXml);

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

        // Bacen SPI messages carry the signature inside the BAH's <Sgntr> element. Fall back to
        // the document root only for envelopes that do not declare one.
        var sgntr = xmlDoc.SelectSingleNode("//*[local-name()='AppHdr']/*[local-name()='Sgntr']")
            ?? xmlDoc.SelectSingleNode("//*[local-name()='Sgntr']");
        var imported = xmlDoc.ImportNode(signatureElement, true);
        if (sgntr is not null)
            sgntr.AppendChild(imported);
        else
            xmlDoc.DocumentElement!.AppendChild(imported);

        return xmlDoc.OuterXml;
    }

    /// <summary>
    /// Removes every XML-DSig <c>&lt;Signature&gt;</c> element from the envelope, leaving the
    /// containing <c>&lt;Sgntr&gt;</c> element in place so a fresh signature can be inserted. Used to
    /// clear a pre-existing Bacen signature before re-signing a response for System B. Returns the
    /// input unchanged (no reserialization) when there is nothing to remove, so unsigned envelopes
    /// such as the pibr.002 echo (empty <c>&lt;Sgntr/&gt;</c>) stay byte-identical.
    /// </summary>
    public static string StripSignatures(string xml)
    {
        var xmlDoc = new XmlDocument { PreserveWhitespace = true };
        xmlDoc.LoadXml(xml);

        var signatureNodes = xmlDoc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl);
        if (signatureNodes.Count == 0)
            return xml;

        // Snapshot first: GetElementsByTagName returns a live list that mutates as we remove nodes.
        foreach (var node in signatureNodes.Cast<XmlNode>().ToList())
            node.ParentNode?.RemoveChild(node);

        return xmlDoc.OuterXml;
    }

    public static bool Verify(string signedXml, X509Certificate2 certificate)
    {
        var xmlDoc = new XmlDocument { PreserveWhitespace = true };
        xmlDoc.LoadXml(signedXml);

        var signatureNodes = xmlDoc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl);
        if (signatureNodes.Count == 0)
            return false;

        var signed = new SignedXml(xmlDoc);
        signed.LoadXml((XmlElement)signatureNodes[0]!);
        return signed.CheckSignature(certificate, verifySignatureOnly: true);
    }
}
