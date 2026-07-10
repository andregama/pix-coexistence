using ConvivenciaPix.Infrastructure.Signing;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ConvivenciaPix.Infrastructure.Hsm;

/// <summary>
/// .NET BCL implementation of IDinamoSdkClient — no native HSM library dependency.
/// Simulates the Dinamo HSM by loading a local PFX file and performing the PIX enveloped signature
/// in software via <see cref="EnvelopedXmlSigner"/>. Used in the Staging environment.
/// In Production, DinamoNetSdkClient (wrapping the real Dinamo.Hsm.DinamoClient) is registered
/// instead, following the same IDinamoSdkClient contract.
///
/// PFX path = DinamoOptions.Host (reused as a file path in this implementation).
/// </summary>
public sealed class LocalDinamoSdkClient : IDinamoSdkClient, IDisposable
{
    private X509Certificate2? _certificate;
    private bool _connected;

    public void Connect(string host, int port, string userId, string password)
    {
        // In this local implementation, "host" is the path to a PFX file.
        // Password maps to the PFX password; userId is ignored (no HSM user concept locally).
        if (!File.Exists(host))
            throw new FileNotFoundException(
                $"LocalDinamoSdkClient: PFX file not found at '{host}'. " +
                "Set Dinamo:Host to a valid PFX file path for Staging.", host);

        _certificate = new X509Certificate2(host, password, X509KeyStorageFlags.Exportable);
        if (_certificate.GetRSAPrivateKey() is null)
            throw new InvalidOperationException(
                $"LocalDinamoSdkClient: PFX at '{host}' does not contain an RSA private key.");

        _connected = true;
    }

    public byte[] SignPIX(string keyId, string certId, byte[] unsignedEnvelope)
    {
        EnsureConnected();
        // Labels are ignored in local mode — the loaded PFX contains exactly one key/cert.
        var signedXml = EnvelopedXmlSigner.Sign(Encoding.UTF8.GetString(unsignedEnvelope), _certificate!);
        return Encoding.UTF8.GetBytes(signedXml);
    }

    public bool VerifyPIX(string chainId, string crl, string signedEnvelope)
    {
        EnsureConnected();
        return EnvelopedXmlSigner.Verify(signedEnvelope, _certificate!);
    }

    public void Disconnect()
    {
        _connected = false;
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
