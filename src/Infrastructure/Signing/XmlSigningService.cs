using ConvivenciaPix.Application.Interfaces;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using System.Xml.Linq;

namespace ConvivenciaPix.Infrastructure.Signing;

public sealed class XmlSigningService : IXmlSigningService
{
    public Task<string> SignAsync(XDocument document, X509Certificate2 certificate)
    {
        var xmlDoc = ToXmlDocument(document);

        var signedXml = new SignedXml(xmlDoc);
        signedXml.SigningKey = certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("Certificate does not contain an RSA private key.");

        var reference = new Reference { Uri = "" };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigC14NTransform());
        signedXml.AddReference(reference);

        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(certificate));
        signedXml.KeyInfo = keyInfo;

        signedXml.ComputeSignature();

        var signatureElement = signedXml.GetXml();
        xmlDoc.DocumentElement!.AppendChild(xmlDoc.ImportNode(signatureElement, true));

        return Task.FromResult(xmlDoc.OuterXml);
    }

    public Task<bool> VerifyAsync(string signedXml, X509Certificate2 certificate)
    {
        var xmlDoc = new XmlDocument { PreserveWhitespace = true };
        xmlDoc.LoadXml(signedXml);

        var signed = new SignedXml(xmlDoc);
        var signatureNode = xmlDoc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl);

        if (signatureNode.Count == 0)
            return Task.FromResult(false);

        signed.LoadXml((XmlElement)signatureNode[0]!);

        return Task.FromResult(signed.CheckSignature(certificate, verifySignatureOnly: true));
    }

    private static XmlDocument ToXmlDocument(XDocument xDocument)
    {
        var xmlDoc = new XmlDocument { PreserveWhitespace = true };
        using var reader = xDocument.CreateReader();
        xmlDoc.Load(reader);
        return xmlDoc;
    }
}
