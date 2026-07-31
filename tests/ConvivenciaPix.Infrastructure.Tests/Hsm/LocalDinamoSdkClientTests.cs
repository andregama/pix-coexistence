using ConvivenciaPix.Infrastructure.Hsm;
using FluentAssertions;
using Microsoft.Extensions.Options;
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

    private LocalDinamoSdkClient BuildSut(string? pfxPath = null) =>
        new(Options.Create(new DinamoOptions { Host = pfxPath ?? _pfxPath, Password = "" }));

    private const string Envelope = """
        <Envelope xmlns="https://www.bcb.gov.br/pi/pibr.002/1.3">
          <AppHdr><MsgDefIdr>pibr.002.spi.1.3</MsgDefIdr><Sgntr/></AppHdr>
          <Document><EchoRpt><EchoTxInf><OrgnlData>x</OrgnlData></EchoTxInf></EchoRpt></Document>
        </Envelope>
        """;

    [Fact]
    public void SignPIX_Output_VerifiesViaVerifyPIX()
    {
        using var sut = BuildSut();

        var signed = sut.SignPIX("key", "cert", Encoding.UTF8.GetBytes(Envelope));
        var signedXml = Encoding.UTF8.GetString(signed);

        signedXml.Should().Contain("Signature");
        sut.VerifyPIX("chain", null, signedXml).Should().BeTrue();
    }

    [Fact]
    public void SignPIXDict_Output_VerifiesViaVerifyPIXDict()
    {
        using var sut = BuildSut();

        var signed = sut.SignPIXDict("key", "cert", Encoding.UTF8.GetBytes("<CreateEntry><Entry>x</Entry></CreateEntry>"));

        Encoding.UTF8.GetString(signed).Should().Contain("Signature");
        sut.VerifyPIXDict("chain", null, signed).Should().BeTrue();
    }

    [Fact]
    public void Operations_MissingPfx_Throw()
    {
        using var sut = BuildSut(Path.Combine(Path.GetTempPath(), "does-not-exist.pfx"));

        var act = () => sut.SignPIX("k", "c", Encoding.UTF8.GetBytes(Envelope));

        act.Should().Throw<FileNotFoundException>();
    }

    public void Dispose()
    {
        if (File.Exists(_pfxPath)) File.Delete(_pfxPath);
    }
}
