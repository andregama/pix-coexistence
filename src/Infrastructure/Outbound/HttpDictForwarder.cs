using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;

namespace ConvivenciaPix.Infrastructure.Outbound;

/// <summary>
/// Development <see cref="IDictForwarder"/> backed by a named <see cref="HttpClient"/>. Used only in
/// Development, where the DICT target is a local stub (e.g. WireMock) reached over plain HTTP — no
/// HSM and no client certificate are involved. Production/Staging use <see cref="DinamoDictForwarder"/>,
/// which performs the mTLS request through the HSM. Copies the method, relative path/query, and
/// forwardable headers, returning upstream status/headers/body verbatim for the use case to re-sign.
/// </summary>
public sealed class HttpDictForwarder : IDictForwarder
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpDictForwarder> _logger;

    public HttpDictForwarder(HttpClient httpClient, ILogger<HttpDictForwarder> logger)
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
            if (DictForwardHeaders.IsHopByHop(name) || name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                continue;
            httpRequest.Headers.TryAddWithoutValidation(name, values);
        }

        _logger.LogHttpDictForward(request.Method, request.PathAndQuery);

        using var response = await _httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken);

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString();

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            if (DictForwardHeaders.IsHopByHop(header.Key))
                continue;
            headers[header.Key] = header.Value.ToArray();
        }

        _logger.LogHttpDictForwarded(request.Method, request.PathAndQuery, (int)response.StatusCode);

        return new DictProxyResponse((int)response.StatusCode, headers, contentType, body);
    }
}

internal static partial class HttpDictForwarderLogMessages
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Forwarding DICT request {Method} {PathAndQuery} to DICT stub")]
    public static partial void LogHttpDictForward(this ILogger logger, string method, string pathAndQuery);

    [LoggerMessage(Level = LogLevel.Information, Message = "DICT request {Method} {PathAndQuery} returned {StatusCode}")]
    public static partial void LogHttpDictForwarded(this ILogger logger, string method, string pathAndQuery, int statusCode);
}
