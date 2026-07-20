using ConvivenciaPix.Infrastructure.Signing;
using FluentAssertions;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Signing;

public sealed class EnvelopedXmlSignerTests
{
    private static readonly X509Certificate2 Cert = CreateSelfSigned();

    private static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
        // Re-import so the private key persists for signing on all platforms.
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    private const string PibrEnvelope = """
        <Envelope xmlns="https://www.bcb.gov.br/pi/pibr.002/1.3">
          <AppHdr>
            <Fr><FIId><FinInstnId><Othr><Id>00038166</Id></Othr></FinInstnId></FIId></Fr>
            <To><FIId><FinInstnId><Othr><Id>99999010</Id></Othr></FinInstnId></FIId></To>
            <BizMsgIdr>M00038166eca1e252ad694dc69df3159</BizMsgIdr>
            <MsgDefIdr>pibr.002.spi.1.3</MsgDefIdr>
            <CreDt>2020-04-07T13:45:48.491Z</CreDt>
            <Sgntr/>
          </AppHdr>
          <Document><EchoRpt><GrpHdr><MsgId>M00038166eca1e252ad694dc69df3159</MsgId>
            <CreDtTm>2020-04-07T13:45:48.491Z</CreDtTm></GrpHdr>
            <EchoTxInf><OrgnlData>Campo livre</OrgnlData></EchoTxInf></EchoRpt></Document>
        </Envelope>
        """;

    // Same envelope, but <Sgntr> already carries a (foreign) Bacen signature, as real inbound
    // responses do. Re-signing must not leave two <Signature> elements behind.
    private const string PreSignedPibrEnvelope = """
        <Envelope xmlns="https://www.bcb.gov.br/pi/pibr.002/1.3">
          <AppHdr>
            <Fr><FIId><FinInstnId><Othr><Id>00038166</Id></Othr></FinInstnId></FIId></Fr>
            <To><FIId><FinInstnId><Othr><Id>99999010</Id></Othr></FinInstnId></FIId></To>
            <BizMsgIdr>M00038166eca1e252ad694dc69df3159</BizMsgIdr>
            <MsgDefIdr>pibr.002.spi.1.3</MsgDefIdr>
            <CreDt>2020-04-07T13:45:48.491Z</CreDt>
            <Sgntr><Signature xmlns="http://www.w3.org/2000/09/xmldsig#"><SignedInfo/><SignatureValue>BACEN-SIG</SignatureValue></Signature></Sgntr>
          </AppHdr>
          <Document><EchoRpt><GrpHdr><MsgId>M00038166eca1e252ad694dc69df3159</MsgId>
            <CreDtTm>2020-04-07T13:45:48.491Z</CreDtTm></GrpHdr>
            <EchoTxInf><OrgnlData>Campo livre</OrgnlData></EchoTxInf></EchoRpt></Document>
        </Envelope>
        """;

    private static int CountSignatures(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return doc.GetElementsByTagName("Signature", "http://www.w3.org/2000/09/xmldsig#").Count;
    }

    [Fact]
    public void Sign_StripsPreExistingSignature_LeavingExactlyOne()
    {
        var signed = EnvelopedXmlSigner.Sign(PreSignedPibrEnvelope, Cert);

        CountSignatures(signed).Should().Be(1, "the Bacen signature must be replaced, not appended to");

        var doc = new XmlDocument();
        doc.LoadXml(signed);
        doc.SelectSingleNode("//*[local-name()='AppHdr']/*[local-name()='Sgntr']/*[local-name()='Signature']")
            .Should().NotBeNull("the single signature must live in AppHdr/Sgntr");
        EnvelopedXmlSigner.Verify(signed, Cert).Should().BeTrue();
    }

    [Fact]
    public void StripSignatures_RemovesSignature_KeepingEmptySgntr()
    {
        var stripped = EnvelopedXmlSigner.StripSignatures(PreSignedPibrEnvelope);

        CountSignatures(stripped).Should().Be(0);
        var doc = new XmlDocument();
        doc.LoadXml(stripped);
        doc.SelectSingleNode("//*[local-name()='AppHdr']/*[local-name()='Sgntr']")
            .Should().NotBeNull("the empty <Sgntr> container must remain so a new signature can be placed");
    }

    [Fact]
    public void StripSignatures_RemovesAllSignatures_WhenMultiplePresent()
    {
        var doubleSigned = PreSignedPibrEnvelope.Replace(
            "</Signature></Sgntr>",
            "</Signature><Signature xmlns=\"http://www.w3.org/2000/09/xmldsig#\"><SignatureValue>SECOND</SignatureValue></Signature></Sgntr>");

        CountSignatures(doubleSigned).Should().Be(2, "sanity: the fixture is double-signed");
        CountSignatures(EnvelopedXmlSigner.StripSignatures(doubleSigned)).Should().Be(0);
    }

    [Fact]
    public void StripSignatures_ReturnsInputUnchanged_WhenNoSignature()
    {
        EnvelopedXmlSigner.StripSignatures(PibrEnvelope).Should().Be(PibrEnvelope);
    }

    [Fact]
    public void Sign_PlacesSignatureInsideAppHdrSgntr()
    {
        var signed = EnvelopedXmlSigner.Sign(PibrEnvelope, Cert);

        var doc = new XmlDocument();
        doc.LoadXml(signed);
        var sig = doc.SelectSingleNode("//*[local-name()='AppHdr']/*[local-name()='Sgntr']/*[local-name()='Signature']");
        sig.Should().NotBeNull("the enveloped signature must live in AppHdr/Sgntr");
    }

    [Fact]
    public void Sign_ThenVerify_ReturnsTrue()
    {
        var signed = EnvelopedXmlSigner.Sign(PibrEnvelope, Cert);
        EnvelopedXmlSigner.Verify(signed, Cert).Should().BeTrue();
    }

    [Fact]
    public void Verify_TamperedPayload_ReturnsFalse()
    {
        var signed = EnvelopedXmlSigner.Sign(PibrEnvelope, Cert);
        var tampered = signed.Replace("Campo livre", "Tampered");
        EnvelopedXmlSigner.Verify(tampered, Cert).Should().BeFalse();
    }

    [Fact]
    public void Verify_NoSignature_ReturnsFalse()
    {
        EnvelopedXmlSigner.Verify(PibrEnvelope, Cert).Should().BeFalse();
    }

    [Fact]
    public void Sign_FallsBackToRoot_WhenNoSgntrElement()
    {
        const string noSgntr = "<Document><Content>x</Content></Document>";
        var signed = EnvelopedXmlSigner.Sign(noSgntr, Cert);

        var doc = new XmlDocument();
        doc.LoadXml(signed);
        doc.SelectSingleNode("/*[local-name()='Document']/*[local-name()='Signature']")
            .Should().NotBeNull();
        EnvelopedXmlSigner.Verify(signed, Cert).Should().BeTrue();
    }
}
