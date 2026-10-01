using ConvivenciaPix.V2.Infrastructure.Signing;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Security.Cryptography.X509Certificates;

namespace ConvivenciaPix.V2.Infrastructure.Hsm;

/// <summary>
/// .NET BCL implementation of IDinamoSdkClient — no native HSM library dependency. Simulates the
/// HSM with a local PFX: SPI envelopes are signed in software via <see cref="EnvelopedXmlSigner"/>
/// and the mTLS request is a software TLS client presenting that PFX. Used in Staging; Production
/// registers DinamoNetSdkClient instead. It verifies only signatures made with its own PFX.
/// PFX path = DinamoOptions.Host (reused as a file path in this implementation).
/// </summary>
public sealed class LocalDinamoSdkClient : IDinamoSdkClient, IDisposable
{
    private readonly Lazy<(HttpClient Http, X509Certificate2 Certificate)> _client;

    public LocalDinamoSdkClient(IOptions<DinamoOptions> options)
    {
        var opts = options.Value;
        // One handler for the lifetime of the process: connections (and TLS sessions) are reused
        // across requests instead of paying a handshake per message.
        _client = new Lazy<(HttpClient, X509Certificate2)>(() =>
        {
            var certificate = LoadCertificate(opts.Host, opts.Password);
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            handler.SslOptions.ClientCertificates = [certificate];
            return (new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, certificate);
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public byte[] SignPIX(string keyId, string certId, byte[] unsignedEnvelope) =>
        EnvelopedXmlSigner.Sign(unsignedEnvelope, _client.Value.Certificate);

    public bool VerifyPIX(string chainId, string? crl, string signedEnvelope) =>
        EnvelopedXmlSigner.Verify(Encoding.UTF8.GetBytes(signedEnvelope), _client.Value.Certificate);

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

        using var timeout = timeoutSeconds > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
            : new CancellationTokenSource();
        using var response = _client.Value.Http.Send(request, HttpCompletionOption.ResponseContentRead, timeout.Token);

        using var buffer = new MemoryStream();
        response.Content.ReadAsStream().CopyTo(buffer);

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in response.Headers)
            headers[h.Key] = h.Value.ToArray();
        foreach (var h in response.Content.Headers)
            headers[h.Key] = h.Value.ToArray();

        return new PixHttpResponse(
            (int)response.StatusCode,
            headers,
            response.Content.Headers.ContentType?.ToString(),
            buffer.ToArray());
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

    public void Dispose()
    {
        if (!_client.IsValueCreated) return;
        _client.Value.Http.Dispose();
        _client.Value.Certificate.Dispose();
    }
}
