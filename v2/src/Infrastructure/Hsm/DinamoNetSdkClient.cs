using Dinamo.Hsm;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace ConvivenciaPix.V2.Infrastructure.Hsm;

/// <summary>
/// Production IDinamoSdkClient wrapping the real <see cref="Dinamo.Hsm.DinamoClient"/>. The managed
/// assembly restores on any platform; the native HSM library is only loaded at runtime, so this type
/// is registered by DI in Production only.
///
/// Dinamo sessions must not be used concurrently across threads, yet reusing a session avoids a full
/// HSM login per request. This client keeps a pool of connected sessions and leases one exclusively
/// for each request; a session that faults is discarded. The number of sessions in use is bounded by
/// the caller (<see cref="Gateway.DinamoSpiGateway"/> limits concurrent requests).
/// </summary>
public sealed class DinamoNetSdkClient : IDinamoSdkClient, IDisposable
{
    private readonly DinamoOptions _options;
    private readonly ConcurrentBag<DinamoClient> _pool = new();
    private volatile bool _disposed;

    public DinamoNetSdkClient(IOptions<DinamoOptions> options)
    {
        _options = options.Value;
    }

    public byte[] SignPIX(string keyId, string certId, byte[] unsignedEnvelope) =>
        Execute(c => c.SignPIX(keyId, certId, unsignedEnvelope));

    public bool VerifyPIX(string chainId, string? crl, string signedEnvelope) =>
        Execute(c => c.VerifyPIX(chainId, crl, signedEnvelope));

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
        bool verifyHostName) => Execute(client =>
    {
        var headers = requestHeaders as string[] ?? requestHeaders.ToArray();
        // The SDK's TimeOut is in milliseconds (0 = no timeout); the option is expressed in seconds.
        var timeoutMs = timeoutSeconds * 1000;
        // With UseGzip the SDK sets Content-Encoding/Accept-Encoding, but the request body must be
        // pre-compressed by the caller.
        var payload = useGzip && body.Length > 0 ? PixGzip.Compress(body) : body;

        var response = method switch
        {
            PixHttpMethod.Post => client.postPIX(
                keyId, certId, serverCertChainId, url, headers, payload, timeoutMs, useGzip, verifyHostName),
            PixHttpMethod.Put => client.putPIX(
                keyId, certId, serverCertChainId, url, headers, payload, timeoutMs, useGzip, verifyHostName),
            PixHttpMethod.Get => client.getPIX(
                keyId, certId, serverCertChainId, url, headers, timeoutMs, useGzip, verifyHostName),
            PixHttpMethod.Delete => client.deletePIX(
                keyId, certId, serverCertChainId, url, headers, timeoutMs, useGzip, verifyHostName),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported PIX HTTP method.")
        };

        // Read the status on the same leased session immediately after the request, before any other
        // operation — the exclusive lease guarantees no other thread's request interleaves here.
        var statusCode = (int)client.getPIXHTTPReqCode();

        var (parsedHeaders, contentType) = PixHeaderParser.Parse(response.Header);
        return new PixHttpResponse(statusCode, parsedHeaders, contentType, response.Body ?? []);
    });

    private T Execute<T>(Func<DinamoClient, T> operation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var client = Rent();
        try
        {
            var result = operation(client);
            Return(client);
            return result;
        }
        catch
        {
            SafeDisconnect(client);
            throw;
        }
    }

    private DinamoClient Rent()
    {
        if (_pool.TryTake(out var pooled))
            return pooled;

        // NOTE: the HSM send/receive socket timeouts are not settable through this managed API
        // (v4.26.0 exposes no setter). Configure them in the Dinamo driver configuration so a
        // network fault to the HSM cannot hang indefinitely.
        var client = new DinamoClient();
        client.Connect(_options.Host, _options.UserId, _options.Password, Encrypted: true, UseLoadBalance: false);
        return client;
    }

    private void Return(DinamoClient client)
    {
        if (_disposed)
            SafeDisconnect(client);
        else
            _pool.Add(client);
    }

    private static void SafeDisconnect(DinamoClient client)
    {
        try { client.Disconnect(); } catch { /* best-effort cleanup of a faulted session */ }
    }

    public void Dispose()
    {
        _disposed = true;
        while (_pool.TryTake(out var client))
            SafeDisconnect(client);
    }
}
