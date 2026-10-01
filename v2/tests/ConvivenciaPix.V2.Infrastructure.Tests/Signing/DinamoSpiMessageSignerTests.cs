using ConvivenciaPix.V2.Infrastructure.Hsm;
using ConvivenciaPix.V2.Infrastructure.Signing;
using ConvivenciaPix.V2.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace ConvivenciaPix.V2.Infrastructure.Tests.Signing;

public sealed class DinamoSpiMessageSignerTests
{
    private static readonly byte[] Unsigned = Encoding.UTF8.GetBytes(SpiXml.Admi004("M0003816612345678901234567890123"));
    private static readonly byte[] HsmOutput = "<signed-by-hsm/>"u8.ToArray();

    private readonly Mock<IDinamoSdkClient> _sdk = new();

    private DinamoSpiMessageSigner Signer(string crl = "") => new(
        _sdk.Object,
        Options.Create(new DinamoOptions { KeyId = "key", CertId = "cert", ChainId = "chain", Crl = crl }),
        NullLogger<DinamoSpiMessageSigner>.Instance);

    [Fact]
    public void Sign_UsesTheConfiguredHsmIdentity_AndReturnsWhatTheHsmProduced()
    {
        _sdk.Setup(s => s.SignPIX("key", "cert", Unsigned)).Returns(HsmOutput);

        Signer().Sign(Unsigned).Should().BeSameAs(HsmOutput);
    }

    [Fact]
    public void Sign_RemovesBacensSignatureBeforeHandingTheMessageToTheHsm()
    {
        var fromBacen = EnvelopedXmlSigner.Sign(Unsigned, TestCertificates.CreateSelfSigned("CN=Bacen"));
        byte[]? handedToHsm = null;
        _sdk.Setup(s => s.SignPIX("key", "cert", It.IsAny<byte[]>()))
            .Callback((string _, string _, byte[] envelope) => handedToHsm = envelope)
            .Returns(HsmOutput);

        Signer().Sign(fromBacen);

        Encoding.UTF8.GetString(handedToHsm!).Should().NotContain("SignatureValue");
        XNode.DeepEquals(
            XDocument.Load(new MemoryStream(handedToHsm!), LoadOptions.PreserveWhitespace),
            XDocument.Load(new MemoryStream(Unsigned), LoadOptions.PreserveWhitespace)).Should().BeTrue();
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("crl-1", "crl-1")]
    public void Verify_ChecksAgainstTheConfiguredChain(string configuredCrl, string? expectedCrl)
    {
        _sdk.Setup(s => s.VerifyPIX("chain", expectedCrl, Encoding.UTF8.GetString(Unsigned))).Returns(true);

        Signer(configuredCrl).Verify(Unsigned).Should().BeTrue();
    }

    [Fact]
    public void Verify_HsmRejects_ReturnsFalse() =>
        Signer().Verify(Unsigned).Should().BeFalse();
}
