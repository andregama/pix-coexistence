using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ConvivenciaPix.Infrastructure.Hsm;

/// <summary>
/// .NET BCL implementation of IDinamoSdkClient — no DinamoAPI.dll dependency.
/// Simulates the Dinamo HSM by loading a local PFX file and performing RSA operations in software.
///
/// Used in Development and Staging environments.
/// In Production: register DinamoNetSdkClient (wraps the real DNET class from DinamoAPI.dll)
/// following the same IDinamoSdkClient contract — no changes to DinamoHsmService required.
///
/// PFX path = DinamoOptions.Host (reused as a file path in this implementation).
/// </summary>
public sealed class LocalDinamoSdkClient : IDinamoSdkClient, IDisposable
{
    private X509Certificate2? _certificate;
    private RSA? _rsaKey;
    private bool _connected;

    public void Connect(string host, int port, string userId, string password)
    {
        // In this local implementation, "host" is the path to a PFX file.
        // Password maps to the PFX password; userId is ignored (no HSM user concept locally).
        if (!File.Exists(host))
            throw new FileNotFoundException(
                $"LocalDinamoSdkClient: PFX file not found at '{host}'. " +
                "Set Dinamo:Host to a valid PFX file path for Development/Staging.", host);

        _certificate = new X509Certificate2(host, password, X509KeyStorageFlags.Exportable);
        _rsaKey = _certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException(
                $"LocalDinamoSdkClient: PFX at '{host}' does not contain an RSA private key.");

        _connected = true;
    }

    public X509Certificate2 GetCertificate(string certificateLabel)
    {
        EnsureConnected();
        // certificateLabel is ignored in local mode — the loaded PFX contains exactly one cert.
        return _certificate!;
    }

    public byte[] Sign(string keyLabel, byte[] data, string mechanism)
    {
        EnsureConnected();

        // mechanism maps to DinamoAPI mechanism constants; only RSA_PKCS1_V1_5 is supported here.
        if (mechanism != "RSA_PKCS1_V1_5")
            throw new NotSupportedException(
                $"LocalDinamoSdkClient: mechanism '{mechanism}' is not supported. Use 'RSA_PKCS1_V1_5'.");

        return _rsaKey!.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    public void Disconnect()
    {
        _connected = false;
        _rsaKey?.Dispose();
        _rsaKey = null;
        _certificate?.Dispose();
        _certificate = null;
    }

    public void Dispose() => Disconnect();

    private void EnsureConnected()
    {
        if (!_connected)
            throw new InvalidOperationException(
                "LocalDinamoSdkClient: Connect() must be called before using the client.");
    }
}
