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

    public byte[] SignPIXDict(string keyId, string certId, byte[] unsignedMessage)
    {
        EnsureConnected();
        // DICT uses a root-enveloped signature; EnvelopedXmlSigner.Sign falls back to the document
        // root when no AppHdr/Sgntr is present, matching the DICT profile in software.
        var signedXml = EnvelopedXmlSigner.Sign(Encoding.UTF8.GetString(unsignedMessage), _certificate!);
        return Encoding.UTF8.GetBytes(signedXml);
    }

    public bool VerifyPIXDict(string chainId, string crl, byte[] signedMessage)
    {
        EnsureConnected();
        return EnvelopedXmlSigner.Verify(Encoding.UTF8.GetString(signedMessage), _certificate!);
    }

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
        EnsureConnected();

        // Software mTLS: present the loaded PFX as the client certificate for https targets, mirroring
        // what the HSM does with its stored key/cert in Production. Labels are ignored locally.
        using var handler = new HttpClientHandler();
        if (url.StartsWith("https", StringComparison.OrdinalIgnoreCase))
            handler.ClientCertificates.Add(_certificate!);

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

        var httpMethod = method switch
        {
            PixHttpMethod.Get => HttpMethod.Get,
            PixHttpMethod.Post => HttpMethod.Post,
            PixHttpMethod.Put => HttpMethod.Put,
            PixHttpMethod.Delete => HttpMethod.Delete,
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported PIX HTTP method.")
        };

        using var request = new HttpRequestMessage(httpMethod, url);
        HttpContent? content = null;
        if (method is PixHttpMethod.Post or PixHttpMethod.Put)
            content = new ByteArrayContent(body);

        foreach (var header in requestHeaders)
        {
            var idx = header.IndexOf(':');
            if (idx <= 0) continue;
            var name = header[..idx].Trim();
            var value = header[(idx + 1)..].Trim();
            if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                content?.Headers.TryAddWithoutValidation(name, value);
            else
                request.Headers.TryAddWithoutValidation(name, value);
        }

        request.Content = content;

        using var response = http.Send(request);
        var responseBody = ReadBody(response.Content);

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in response.Headers)
            headers[h.Key] = h.Value.ToArray();
        foreach (var h in response.Content.Headers)
            headers[h.Key] = h.Value.ToArray();

        return new PixHttpResponse(
            (int)response.StatusCode,
            headers,
            response.Content.Headers.ContentType?.ToString(),
            responseBody);
    }

    private static byte[] ReadBody(HttpContent content)
    {
        using var stream = content.ReadAsStream();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
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
