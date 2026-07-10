using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

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
