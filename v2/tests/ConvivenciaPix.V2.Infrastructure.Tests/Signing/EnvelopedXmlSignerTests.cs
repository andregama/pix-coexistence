using ConvivenciaPix.V2.Infrastructure.Signing;
using ConvivenciaPix.V2.Tests.Shared;
using FluentAssertions;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace ConvivenciaPix.V2.Infrastructure.Tests.Signing;

public sealed class EnvelopedXmlSignerTests
{
    private static readonly X509Certificate2 Bank = TestCertificates.CreateSelfSigned("CN=Bank");
    private static readonly X509Certificate2 Bacen = TestCertificates.CreateSelfSigned("CN=Bacen");

    private static byte[] Message(string data = "Campo livre") =>
        Encoding.UTF8.GetBytes(SpiXml.Pibr002("M00038166eca1e252ad694dc69df3159", data));

    private static XmlDocument Parse(byte[] xml)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        document.Load(new MemoryStream(xml));
        return document;
    }

    private static XDocument Load(byte[] xml) => XDocument.Load(new MemoryStream(xml), LoadOptions.PreserveWhitespace);

    private static XmlNodeList Signatures(byte[] xml) =>
        Parse(xml).GetElementsByTagName("Signature", "http://www.w3.org/2000/09/xmldsig#");

    [Fact]
    public void Sign_PlacesOneSignatureInTheHeader_AndItVerifies()
    {
        var signed = EnvelopedXmlSigner.Sign(Message(), Bank);

        var signatures = Signatures(signed);
        signatures.Count.Should().Be(1);
        signatures[0]!.ParentNode!.LocalName.Should().Be("Sgntr");
        signatures[0]!.ParentNode!.ParentNode!.LocalName.Should().Be("AppHdr");
        EnvelopedXmlSigner.Verify(signed, Bank).Should().BeTrue();
    }

    [Fact]
    public void Sign_MessageAlreadySignedByBacen_ReplacesTheSignature()
    {
        var fromBacen = EnvelopedXmlSigner.Sign(Message(), Bacen);

        var delivered = EnvelopedXmlSigner.Sign(fromBacen, Bank);

        Signatures(delivered).Count.Should().Be(1);
        EnvelopedXmlSigner.Verify(delivered, Bank).Should().BeTrue();
        EnvelopedXmlSigner.Verify(delivered, Bacen).Should().BeFalse();
    }

    [Fact]
    public void Sign_LeavesTheBusinessContentUntouched()
    {
        var original = Message();

        var signed = EnvelopedXmlSigner.Sign(original, Bank);

        // Without the signature, the delivered message is, node for node, what was received.
        XNode.DeepEquals(Load(EnvelopedXmlSigner.StripSignatures(signed)), Load(original)).Should().BeTrue();
    }

    [Fact]
    public void Verify_ContentChangedAfterSigning_Fails()
    {
        var signed = Encoding.UTF8.GetString(EnvelopedXmlSigner.Sign(Message("Campo livre"), Bank));

        var tampered = Encoding.UTF8.GetBytes(signed.Replace("Campo livre", "Campo alterado"));

        EnvelopedXmlSigner.Verify(tampered, Bank).Should().BeFalse();
    }

    [Fact]
    public void Verify_UnsignedMessage_Fails() =>
        EnvelopedXmlSigner.Verify(Message(), Bank).Should().BeFalse();

    [Fact]
    public void StripSignatures_NothingToRemove_ReturnsTheSameBytes()
    {
        var unsigned = Message();

        EnvelopedXmlSigner.StripSignatures(unsigned).Should().BeSameAs(unsigned);
    }

    [Fact]
    public void Sign_HonoursAByteOrderMarkAndNonAsciiContent()
    {
        var withBom = Encoding.UTF8.GetPreamble().Concat(Message("São Paulo — ação")).ToArray();

        var signed = EnvelopedXmlSigner.Sign(withBom, Bank);

        EnvelopedXmlSigner.Verify(signed, Bank).Should().BeTrue();
        Encoding.UTF8.GetString(signed).Should().Contain("São Paulo — ação");
    }
}
