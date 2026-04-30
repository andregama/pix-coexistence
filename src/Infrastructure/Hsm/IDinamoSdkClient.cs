using System.Security.Cryptography.X509Certificates;

namespace ConvivenciaPix.Infrastructure.Hsm;

/// <summary>
/// Mirrors the DinamoAPI.NET (DNET) contract for HSM operations used by this solution.
/// Implementations:
///   - LocalDinamoSdkClient: .NET BCL only — no DinamoAPI.dll required. Used in Development and Staging.
///   - DinamoNetSdkClient:   wraps the real DNET class from DinamoAPI.dll. Plugged in by ops at Production deploy.
///
/// Method signatures match the DinamoAPI.NET SDK exactly so swapping implementations requires no changes
/// to DinamoHsmService or any caller.
/// </summary>
public interface IDinamoSdkClient
{
    /// <summary>
    /// Opens an authenticated session to the HSM.
    /// Equivalent to DNET.Open(host, port, userId, password, encrypted: true).
    /// </summary>
    void Connect(string host, int port, string userId, string password);

    /// <summary>
    /// Retrieves the X.509 certificate associated with the given label from the HSM key store.
    /// Equivalent to DNET.GetUserCertificate(certificateLabel).
    /// </summary>
    X509Certificate2 GetCertificate(string certificateLabel);

    /// <summary>
    /// Signs <paramref name="data"/> using the private key identified by <paramref name="keyLabel"/>.
    /// Equivalent to DNET.DSPKISign(keyLabel, data, mechanism).
    /// Supported mechanism values: "RSA_PKCS1_V1_5".
    /// </summary>
    byte[] Sign(string keyLabel, byte[] data, string mechanism);

    /// <summary>
    /// Closes the HSM session and releases the connection.
    /// Equivalent to DNET.Close().
    /// </summary>
    void Disconnect();
}
