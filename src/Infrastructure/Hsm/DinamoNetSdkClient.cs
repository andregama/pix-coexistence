using Dinamo.Hsm;

namespace ConvivenciaPix.Infrastructure.Hsm;

/// <summary>
/// Production IDinamoSdkClient wrapping the real <see cref="Dinamo.Hsm.DinamoClient"/> from the
/// Dinamo.Hsm package. The managed assembly restores on any platform; the underlying native HSM
/// library is only loaded at runtime, so this type is registered by DI in Production only.
///
/// The HSM performs the full PIX/SPI enveloped signature and verification internally — keys and
/// certificates are referenced by id and never leave the HSM.
/// </summary>
public sealed class DinamoNetSdkClient : IDinamoSdkClient, IDisposable
{
    private DinamoClient? _client;

    public void Connect(string host, int port, string userId, string password)
    {
        // DinamoClient.Connect takes the address (host), not a separate port; the SDK uses the
        // default management port. An encrypted, non-load-balanced session is used for signing.
        _client = new DinamoClient();
        _client.Connect(host, userId, password, Encrypted: true, UseLoadBalance: false);
    }

    public byte[] SignPIX(string keyId, string certId, byte[] unsignedEnvelope) =>
        Client.SignPIX(keyId, certId, unsignedEnvelope);

    public bool VerifyPIX(string chainId, string crl, string signedEnvelope) =>
        Client.VerifyPIX(chainId, crl, signedEnvelope);

    public void Disconnect()
    {
        _client?.Disconnect();
        _client = null;
    }

    public void Dispose() => Disconnect();

    private DinamoClient Client => _client
        ?? throw new InvalidOperationException("DinamoNetSdkClient: Connect() must be called before use.");
}
