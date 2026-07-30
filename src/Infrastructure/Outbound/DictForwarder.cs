using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;

namespace ConvivenciaPix.Infrastructure.Outbound;

/// <summary>
/// <see cref="IDictForwarder"/> backed by a named <see cref="HttpClient"/> configured for outbound
/// mTLS to the real DICT API (client certificate + resilience handler are wired in DI). Copies the
/// method, relative path/query, and forwardable headers from the captured request, and returns the
/// upstream status, headers, and body verbatim for the use case to (re-)sign.
/// </summary>
public sealed class DictForwarder : IDictForwarder
{
    // Headers that are connection-specific or recomputed by HttpClient — never forwarded as-is.
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Content-Length"
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<DictForwarder> _logger;

    public DictForwarder(HttpClient httpClient, ILogger<DictForwarder> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<DictProxyResponse> SendAsync(
        DictProxyRequest request, CancellationToken cancellationToken = default)
    {
        using var httpRequest = new HttpRequestMessage(
            new HttpMethod(request.Method),
            request.PathAndQuery.TrimStart('/'));

        if (request.Body.Length > 0)
        {
            httpRequest.Content = new ByteArrayContent(request.Body);
            if (!string.IsNullOrEmpty(request.ContentType)
                && MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType))
            {
                httpRequest.Content.Headers.ContentType = mediaType;
            }
        }

        foreach (var (name, values) in request.Headers)
        {
            if (HopByHopHeaders.Contains(name) || name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                continue;
            httpRequest.Headers.TryAddWithoutValidation(name, values);
        }

        _logger.LogDictForward(request.Method, request.PathAndQuery);

        using var response = await _httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken);

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString();

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key))
                continue;
            headers[header.Key] = header.Value.ToArray();
        }

        _logger.LogDictForwarded(request.Method, request.PathAndQuery, (int)response.StatusCode);

        return new DictProxyResponse((int)response.StatusCode, headers, contentType, body);
    }
}

internal static partial class DictForwarderLogMessages
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Forwarding DICT request {Method} {PathAndQuery} to real DICT API")]
    public static partial void LogDictForward(this ILogger logger, string method, string pathAndQuery);

    [LoggerMessage(Level = LogLevel.Information, Message = "DICT request {Method} {PathAndQuery} returned {StatusCode}")]
    public static partial void LogDictForwarded(this ILogger logger, string method, string pathAndQuery, int statusCode);
}
