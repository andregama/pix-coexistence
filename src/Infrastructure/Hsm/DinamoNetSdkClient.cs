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

    public byte[] SignPIXDict(string keyId, string certId, byte[] unsignedMessage) =>
        Client.SignPIXDict(keyId, certId, unsignedMessage);

    public bool VerifyPIXDict(string chainId, string crl, byte[] signedMessage) =>
        Client.VerifyPIXDict(chainId, crl, signedMessage);

    public PixHttpResponse SendPix(
        PixHttpMethod method,
        string keyId,
        string certId,
        string serverCertChainId,
        string url,
        IReadOnlyList<string> requestHeaders,
        byte[] body,
        int timeoutSeconds,
        bool useGzip,
        bool verifyHostName)
    {
        var headers = requestHeaders as string[] ?? requestHeaders.ToArray();
        // The SDK's TimeOut maps to the libcurl CURLOPT_TIMEOUT (whole seconds), matching the name
        // (no "Ms" suffix), so the configured seconds value is passed through unscaled.
        var response = method switch
        {
            PixHttpMethod.Post => Client.postPIX(
                keyId, certId, serverCertChainId, url, headers, body, timeoutSeconds, useGzip, verifyHostName),
            PixHttpMethod.Put => Client.putPIX(
                keyId, certId, serverCertChainId, url, headers, body, timeoutSeconds, useGzip, verifyHostName),
            PixHttpMethod.Get => Client.getPIX(
                keyId, certId, serverCertChainId, url, headers, timeoutSeconds, useGzip, verifyHostName),
            PixHttpMethod.Delete => Client.deletePIX(
                keyId, certId, serverCertChainId, url, headers, timeoutSeconds, useGzip, verifyHostName),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported PIX HTTP method.")
        };

        // Must read the status on the same session immediately after the request, before any other op.
        var statusCode = (int)Client.getPIXHTTPReqCode();

        var (parsedHeaders, contentType) = PixHeaderParser.Parse(response.Header);
        return new PixHttpResponse(statusCode, parsedHeaders, contentType, response.Body ?? []);
    }

    public void Disconnect()
    {
        _client?.Disconnect();
        _client = null;
    }

    public void Dispose() => Disconnect();

    private DinamoClient Client => _client
        ?? throw new InvalidOperationException("DinamoNetSdkClient: Connect() must be called before use.");
}
