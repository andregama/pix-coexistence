using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Infrastructure.Hsm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace ConvivenciaPix.Infrastructure.Outbound;

/// <summary>
/// Production/Staging <see cref="IDictForwarder"/> that performs the outbound mTLS request through the
/// HSM via <see cref="IDinamoSdkClient.SendPix"/> (Dinamo postPIX/getPIX/...). The HSM owns the TLS
/// handshake and presents the bank's client certificate by label — no certificate is handled in app
/// code. Retries/timeout come from an injected Polly <see cref="ResiliencePipeline"/> (RF-09).
/// </summary>
public sealed class DinamoDictForwarder : IDictForwarder
{
    // The HSM session is not safe to share across threads, and the SDK requires the status read to
    // immediately follow the request on the same session — serialize connect→send→disconnect.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly IDinamoSdkClient _sdk;
    private readonly DinamoOptions _dinamo;
    private readonly DictProxyOptions _options;
    private readonly ResiliencePipeline _pipeline;
    private readonly ILogger<DinamoDictForwarder> _logger;

    public DinamoDictForwarder(
        IDinamoSdkClient sdk,
        IOptions<DinamoOptions> dinamoOptions,
        IOptions<DictProxyOptions> options,
        [FromKeyedServices(DictResilience.Key)] ResiliencePipeline pipeline,
        ILogger<DinamoDictForwarder> logger)
    {
        _sdk = sdk;
        _dinamo = dinamoOptions.Value;
        _options = options.Value;
        _pipeline = pipeline;
        _logger = logger;
    }

    public async Task<DictProxyResponse> SendAsync(
        DictProxyRequest request, CancellationToken cancellationToken = default)
    {
        var pixMethod = MapMethod(request.Method);
        var url = BuildUrl(_options.BaseUrl, request.PathAndQuery);
        var headers = BuildHeaderList(request);

        _logger.LogDinamoDictForward(request.Method, request.PathAndQuery);

        var response = await _pipeline.ExecuteAsync(async ct =>
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return SendThroughHsm(pixMethod, url, headers, request.Body);
            }
            finally
            {
                _gate.Release();
            }
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogDinamoDictForwarded(request.Method, request.PathAndQuery, response.StatusCode);
        return response;
    }

    private DictProxyResponse SendThroughHsm(
        PixHttpMethod method, string url, IReadOnlyList<string> headers, byte[] body)
    {
        _sdk.Connect(_dinamo.Host, _dinamo.Port, _dinamo.UserId, _dinamo.Password);
        try
        {
            var pix = _sdk.SendPix(
                method,
                _options.MtlsKeyId,
                _options.MtlsCertId,
                _options.ServerCertChainId,
                url,
                headers,
                body,
                _options.TimeoutSeconds,
                _options.UseGzip,
                _options.VerifyHostName);

            var headerDict = pix.Headers.ToDictionary(
                kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            return new DictProxyResponse(pix.StatusCode, headerDict, pix.ContentType, pix.Body);
        }
        finally
        {
            _sdk.Disconnect();
        }
    }

    private static string BuildUrl(string baseUrl, string pathAndQuery)
    {
        var trimmedBase = baseUrl.TrimEnd('/');
        var path = pathAndQuery.StartsWith('/') ? pathAndQuery : "/" + pathAndQuery;
        return trimmedBase + path;
    }

    // Format headers as Dinamo's "Name: Value" entries (no CRLF). Drop hop-by-hop and any incoming
    // Content-* headers; re-add Content-Type explicitly (the SDK has no separate content-type param).
    private static List<string> BuildHeaderList(DictProxyRequest request)
    {
        var list = new List<string>();
        foreach (var (name, values) in request.Headers)
        {
            if (DictForwardHeaders.IsHopByHop(name)
                || name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                continue;
            list.Add($"{name}: {string.Join(",", values)}");
        }

        if (request.Body.Length > 0 && !string.IsNullOrEmpty(request.ContentType))
            list.Add($"Content-Type: {request.ContentType}");

        return list;
    }

    private static PixHttpMethod MapMethod(string method) => method.ToUpperInvariant() switch
    {
        "GET" => PixHttpMethod.Get,
        "POST" => PixHttpMethod.Post,
        "PUT" => PixHttpMethod.Put,
        "DELETE" => PixHttpMethod.Delete,
        _ => throw new NotSupportedException($"DICT proxy does not support HTTP method '{method}'.")
    };
}

internal static partial class DinamoDictForwarderLogMessages
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Forwarding DICT request {Method} {PathAndQuery} via HSM (Dinamo PIX)")]
    public static partial void LogDinamoDictForward(this ILogger logger, string method, string pathAndQuery);

    [LoggerMessage(Level = LogLevel.Information, Message = "DICT request {Method} {PathAndQuery} returned {StatusCode}")]
    public static partial void LogDinamoDictForwarded(this ILogger logger, string method, string pathAndQuery, int statusCode);
}
