using ConvivenciaPix.Infrastructure.Signing;
using Microsoft.Extensions.Options;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ConvivenciaPix.Infrastructure.Hsm;

/// <summary>
/// .NET BCL implementation of IDinamoSdkClient — no native HSM library dependency. Simulates the
/// Dinamo HSM by loading a local PFX file and performing the PIX enveloped signature in software via
/// <see cref="EnvelopedXmlSigner"/>; outbound PIX HTTP is a software mTLS call presenting the PFX.
/// Used in the Staging environment. In Production, DinamoNetSdkClient (wrapping the real
/// Dinamo.Hsm.DinamoClient) is registered instead, following the same contract.
///
/// The certificate is loaded once, lazily and thread-safely; every operation is self-contained and
/// stateless, so the single registered instance is safe to use concurrently.
/// PFX path = DinamoOptions.Host (reused as a file path in this implementation).
/// </summary>
public sealed class LocalDinamoSdkClient : IDinamoSdkClient, IDisposable
{
    private readonly Lazy<X509Certificate2> _certificate;

    public LocalDinamoSdkClient(IOptions<DinamoOptions> options)
    {
        var opts = options.Value;
        _certificate = new Lazy<X509Certificate2>(
            () => LoadCertificate(opts.Host, opts.Password),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public byte[] SignPIX(string keyId, string certId, byte[] unsignedEnvelope) =>
        Encoding.UTF8.GetBytes(EnvelopedXmlSigner.Sign(Encoding.UTF8.GetString(unsignedEnvelope), _certificate.Value));

    public bool VerifyPIX(string chainId, string? crl, string signedEnvelope) =>
        EnvelopedXmlSigner.Verify(signedEnvelope, _certificate.Value);

    public byte[] SignPIXDict(string keyId, string certId, byte[] unsignedMessage) =>
        // DICT uses a root-enveloped signature; EnvelopedXmlSigner.Sign falls back to the document
        // root when no AppHdr/Sgntr is present, matching the DICT profile in software.
        Encoding.UTF8.GetBytes(EnvelopedXmlSigner.Sign(Encoding.UTF8.GetString(unsignedMessage), _certificate.Value));

    public bool VerifyPIXDict(string chainId, string? crl, byte[] signedMessage) =>
        EnvelopedXmlSigner.Verify(Encoding.UTF8.GetString(signedMessage), _certificate.Value);

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
        // Software mTLS: present the loaded PFX as the client certificate for https targets, mirroring
        // what the HSM does with its stored key/cert in Production.
        using var handler = new HttpClientHandler();
        if (url.StartsWith("https", StringComparison.OrdinalIgnoreCase))
            handler.ClientCertificates.Add(_certificate.Value);

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
        {
            var payload = useGzip && body.Length > 0 ? PixGzip.Compress(body) : body;
            content = new ByteArrayContent(payload);
            if (useGzip && body.Length > 0)
                content.Headers.ContentEncoding.Add("gzip");
        }

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

    private static X509Certificate2 LoadCertificate(string pfxPath, string password)
    {
        if (!File.Exists(pfxPath))
            throw new FileNotFoundException(
                $"LocalDinamoSdkClient: PFX file not found at '{pfxPath}'. " +
                "Set Dinamo:Host to a valid PFX file path for Staging.", pfxPath);

        var certificate = new X509Certificate2(pfxPath, password, X509KeyStorageFlags.Exportable);
        if (certificate.GetRSAPrivateKey() is null)
            throw new InvalidOperationException(
                $"LocalDinamoSdkClient: PFX at '{pfxPath}' does not contain an RSA private key.");

        return certificate;
    }

    private static byte[] ReadBody(HttpContent content)
    {
        using var stream = content.ReadAsStream();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public void Dispose()
    {
        if (_certificate.IsValueCreated)
            _certificate.Value.Dispose();
    }
}
