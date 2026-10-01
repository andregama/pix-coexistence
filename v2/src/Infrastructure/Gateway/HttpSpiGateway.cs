using ConvivenciaPix.V2.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;

namespace ConvivenciaPix.V2.Infrastructure.Gateway;

/// <summary>
/// <see cref="ISpiGateway"/> over a plain <see cref="HttpClient"/>. Used in Development, where the
/// target is the local SPI simulator — no HSM and no client certificate are involved.
/// </summary>
public sealed class HttpSpiGateway : ISpiGateway
{
    public const string HttpClientName = "spi-gateway";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SpiGatewayOptions _options;
    private readonly ILogger<HttpSpiGateway> _logger;

    public HttpSpiGateway(
        IHttpClientFactory httpClientFactory, IOptions<SpiGatewayOptions> options, ILogger<HttpSpiGateway> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SpiGatewayResponse> SendAsync(SpiGatewayRequest request, CancellationToken cancellationToken = default)
    {
        var url = SpiGatewayUrl.Build(_options.BaseUrl, request.PathAndQuery);
        using var httpRequest = new HttpRequestMessage(new HttpMethod(request.Method), url);

        if (request.Body.Length > 0)
        {
            httpRequest.Content = new ByteArrayContent(request.Body);
            if (MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType))
                httpRequest.Content.Headers.ContentType = mediaType;
        }

        foreach (var (name, value) in request.Headers)
            httpRequest.Headers.TryAddWithoutValidation(name, value);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, timeout.Token);
            var body = await response.Content.ReadAsByteArrayAsync(timeout.Token);

            var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers)
                headers[header.Key] = header.Value.ToArray();

            _logger.LogSpiResponse(request.Method, request.PathAndQuery, (int)response.StatusCode);
            return new SpiGatewayResponse(
                (int)response.StatusCode, headers, response.Content.Headers.ContentType?.ToString(), body);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SpiGatewayTimeoutException(
                $"The SPI did not answer {request.Method} {request.PathAndQuery} within {request.TimeoutSeconds}s.", ex);
        }
    }
}

internal static class SpiGatewayUrl
{
    public static string Build(string baseUrl, string pathAndQuery)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("SpiGateway:BaseUrl is required.");
        return baseUrl.TrimEnd('/') + (pathAndQuery.StartsWith('/') ? pathAndQuery : "/" + pathAndQuery);
    }
}

internal static partial class SpiGatewayLogMessages
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "SPI {Method} {PathAndQuery} returned {StatusCode}")]
    public static partial void LogSpiResponse(this ILogger logger, string method, string pathAndQuery, int statusCode);
}
