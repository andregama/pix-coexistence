using ConvivenciaPix.Infrastructure.Hsm;
using FluentAssertions;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Hsm;

public sealed class LocalDinamoSdkClientTests : IDisposable
{
    private readonly string _pfxPath = Path.Combine(Path.GetTempPath(), $"local-hsm-{Guid.NewGuid():N}.pfx");

    public LocalDinamoSdkClientTests()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=LocalHsmTest", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllBytes(_pfxPath, cert.Export(X509ContentType.Pfx));
    }

    private const string Envelope = """
        <Envelope xmlns="https://www.bcb.gov.br/pi/pibr.002/1.3">
          <AppHdr><MsgDefIdr>pibr.002.spi.1.3</MsgDefIdr><Sgntr/></AppHdr>
          <Document><EchoRpt><EchoTxInf><OrgnlData>x</OrgnlData></EchoTxInf></EchoRpt></Document>
        </Envelope>
        """;

    [Fact]
    public void SignPIX_Output_VerifiesViaVerifyPIX()
    {
        using var sut = new LocalDinamoSdkClient();
        sut.Connect(_pfxPath, 0, "unused", "");

        var signed = sut.SignPIX("key", "cert", Encoding.UTF8.GetBytes(Envelope));
        var signedXml = Encoding.UTF8.GetString(signed);

        signedXml.Should().Contain("Signature");
        sut.VerifyPIX("chain", "", signedXml).Should().BeTrue();
    }

    [Fact]
    public void Operations_BeforeConnect_Throw()
    {
        using var sut = new LocalDinamoSdkClient();
        var act = () => sut.SignPIX("k", "c", Encoding.UTF8.GetBytes(Envelope));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Connect_MissingPfx_Throws()
    {
        using var sut = new LocalDinamoSdkClient();
        var act = () => sut.Connect(Path.Combine(Path.GetTempPath(), "does-not-exist.pfx"), 0, "u", "p");
        act.Should().Throw<FileNotFoundException>();
    }

    public void Dispose()
    {
        if (File.Exists(_pfxPath)) File.Delete(_pfxPath);
    }
}
