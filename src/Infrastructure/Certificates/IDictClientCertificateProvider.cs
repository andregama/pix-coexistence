using System.Security.Cryptography.X509Certificates;

namespace ConvivenciaPix.Infrastructure.Certificates;

/// <summary>
/// Supplies the client certificate the DICT proxy presents to the real DICT API for outbound mTLS.
/// In Production the private key is HSM-backed (Dinamo CNG KSP) and never leaves the HSM; in
/// Dev/Staging a local PFX is used. Implementations are environment-switched in DI, mirroring the
/// HSM signing services.
/// </summary>
public interface IDictClientCertificateProvider
{
    X509Certificate2 GetClientCertificate();
}
