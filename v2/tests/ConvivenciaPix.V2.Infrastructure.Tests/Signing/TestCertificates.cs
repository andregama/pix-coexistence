using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ConvivenciaPix.V2.Infrastructure.Tests.Signing;

internal static class TestCertificates
{
    public static X509Certificate2 CreateSelfSigned(string subject = "CN=Test")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
        // Re-import so the private key persists for signing on all platforms.
        return new X509Certificate2(certificate.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }
}
