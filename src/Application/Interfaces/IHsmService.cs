using System.Security.Cryptography.X509Certificates;

namespace ConvivenciaPix.Application.Interfaces;

/// <summary>
/// Abstraction over HSM hardware (Dinamo in production, self-signed PFX in development).
/// All XML signing operations must go through this interface — never access the HSM SDK directly.
/// </summary>
public interface IHsmService
{
    Task<X509Certificate2> GetSigningCertificateAsync(CancellationToken cancellationToken = default);
    Task<byte[]> SignDataAsync(byte[] data, CancellationToken cancellationToken = default);
}
