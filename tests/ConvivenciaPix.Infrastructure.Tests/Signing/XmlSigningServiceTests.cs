using ConvivenciaPix.Infrastructure.Signing;
using FluentAssertions;
using Xunit;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

namespace ConvivenciaPix.Infrastructure.Tests.Signing;

public sealed class XmlSigningServiceTests
{
    private static readonly X509Certificate2 Cert = CreateSelfSignedCert();
    private readonly XmlSigningService _service = new();

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // Export and re-import with private key accessible on all platforms
        return X509Certificate2.CreateFromPem(
            cert.ExportCertificatePem(),
            rsa.ExportRSAPrivateKeyPem());
    }

    private static XDocument SampleDocument() =>
        XDocument.Parse("<Document><Content>Hello SPI</Content></Document>");

    [Fact]
    public async Task SignAsync_AddsSignatureElement()
    {
        var signed = await _service.SignAsync(SampleDocument(), Cert);
        signed.Should().Contain("<Signature");
    }

    [Fact]
    public async Task VerifyAsync_ValidSignature_ReturnsTrue()
    {
        var signed = await _service.SignAsync(SampleDocument(), Cert);
        var result = await _service.VerifyAsync(signed, Cert);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_TamperedPayload_ReturnsFalse()
    {
        var signed = await _service.SignAsync(SampleDocument(), Cert);
        var tampered = signed.Replace("Hello SPI", "Tampered Content");
        var result = await _service.VerifyAsync(tampered, Cert);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyAsync_NoSignaturePresent_ReturnsFalse()
    {
        var result = await _service.VerifyAsync("<Document><Content>unsigned</Content></Document>", Cert);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task SignThenVerify_RoundTrip_ReturnsTrue()
    {
        var doc = XDocument.Parse("<Pacs008><Amount>1000.00</Amount><Payee>ITAU</Payee></Pacs008>");
        var signed = await _service.SignAsync(doc, Cert);
        var verified = await _service.VerifyAsync(signed, Cert);
        verified.Should().BeTrue();
    }
}
