using ConvivenciaPix.V2.Application.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ConvivenciaPix.V2.Infrastructure.Signing;

/// <summary>
/// Development-only stand-in for the HSM. Signs with a self-signed PFX under <c>v2/certs/dev/</c>,
/// created on first use. It must never be registered outside Development — the constructor refuses.
/// It can only verify its own signatures, not Bacen's.
/// </summary>
public sealed class MockSpiMessageSigner : ISpiMessageSigner
{
    private readonly Lazy<X509Certificate2> _certificate;

    public MockSpiMessageSigner(IHostEnvironment environment, ILogger<MockSpiMessageSigner> logger)
    {
        if (!environment.IsDevelopment())
            throw new InvalidOperationException("MockSpiMessageSigner must not be used outside of Development environments.");

        var pfxPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "certs", "dev", "mock-hsm.pfx"));
        _certificate = new Lazy<X509Certificate2>(() => LoadOrCreate(pfxPath, logger), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public byte[] Sign(byte[] xml) => EnvelopedXmlSigner.Sign(xml, _certificate.Value);

    public bool Verify(byte[] xml) => EnvelopedXmlSigner.Verify(xml, _certificate.Value);

    private static X509Certificate2 LoadOrCreate(string pfxPath, ILogger logger)
    {
        if (!File.Exists(pfxPath))
        {
            logger.LogGeneratingDevCert(pfxPath);

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=ConvivenciaPix-V2-Dev-MockHSM", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));

            // Write to a temporary name first: the API and the worker may both start on a clean checkout.
            Directory.CreateDirectory(Path.GetDirectoryName(pfxPath)!);
            var temporary = pfxPath + "." + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temporary, created.Export(X509ContentType.Pfx));
            try { File.Move(temporary, pfxPath, overwrite: false); }
            catch (IOException) { File.Delete(temporary); }
        }

        logger.LogMockSignerLoaded(pfxPath);
        return new X509Certificate2(pfxPath, (string?)null, X509KeyStorageFlags.Exportable);
    }
}

internal static partial class MockSpiMessageSignerLogMessages
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "MockSpiMessageSigner loaded dev certificate from {PfxPath}. DO NOT use in production.")]
    public static partial void LogMockSignerLoaded(this ILogger logger, string pfxPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Generating self-signed dev certificate at {PfxPath}")]
    public static partial void LogGeneratingDevCert(this ILogger logger, string pfxPath);
}
