using ConvivenciaPix.Application.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ConvivenciaPix.Infrastructure.Signing;

/// <summary>
/// Development-only HSM mock. Loads a self-signed PFX from certs/dev/.
/// This class must never be registered in non-Development environments —
/// the DI registration guard in InfrastructureServiceExtensions enforces this.
/// </summary>
public sealed class MockHsmService : IHsmService
{
    private readonly string _pfxPath;
    private readonly ILogger<MockHsmService> _logger;
    private X509Certificate2? _cachedCert;

    public MockHsmService(IHostEnvironment environment, ILogger<MockHsmService> logger)
    {
        if (!environment.IsDevelopment())
            throw new InvalidOperationException(
                "MockHsmService must not be used outside of Development environments.");

        _pfxPath = Path.Combine(environment.ContentRootPath, "..", "..", "certs", "dev", "mock-hsm.pfx");
        _logger = logger;
    }

    public Task<X509Certificate2> GetSigningCertificateAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedCert is null)
        {
            if (!File.Exists(_pfxPath))
                GenerateSelfSignedCert();

            _cachedCert = new X509Certificate2(_pfxPath, (string?)null, X509KeyStorageFlags.Exportable);
            _logger.LogMockHsmLoaded(_pfxPath);
        }

        return Task.FromResult(_cachedCert);
    }

    public async Task<byte[]> SignDataAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        var cert = await GetSigningCertificateAsync(cancellationToken);
        using var rsa = cert.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("Dev cert has no RSA private key.");
        return rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private void GenerateSelfSignedCert()
    {
        _logger.LogGeneratingDevCert(_pfxPath);

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=ConvivenciaPix-Dev-MockHSM",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));

        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));

        Directory.CreateDirectory(Path.GetDirectoryName(_pfxPath)!);
        File.WriteAllBytes(_pfxPath, cert.Export(X509ContentType.Pfx));
    }
}

internal static partial class MockHsmServiceLogMessages
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "MockHsmService loaded dev certificate from {PfxPath}. DO NOT use in production.")]
    public static partial void LogMockHsmLoaded(this ILogger logger, string pfxPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Generating self-signed dev certificate at {PfxPath}")]
    public static partial void LogGeneratingDevCert(this ILogger logger, string pfxPath);
}
