using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.Tracing;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace ConvivenciaPix.Application.UseCases.ProxyDict;

public sealed class ProxyDictRequestUseCase : IProxyDictRequestUseCase
{
    private readonly IHsmService _hsmService;
    private readonly IDictForwarder _forwarder;
    private readonly ISpiMetrics _metrics;
    private readonly ILogger<ProxyDictRequestUseCase> _logger;

    public ProxyDictRequestUseCase(
        IHsmService hsmService,
        IDictForwarder forwarder,
        ISpiMetrics metrics,
        ILogger<ProxyDictRequestUseCase> logger)
    {
        _hsmService = hsmService;
        _forwarder = forwarder;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<DictProxyResponse> ExecuteAsync(
        DictProxyRequest request, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        // 1. Re-sign the request body with the bank's HSM DICT identity so the real DICT API accepts
        //    it. Non-XML or empty bodies (e.g. GET/DELETE lookups) pass through untouched.
        var outboundBody = request.Body;
        if (HasXmlBody(request.ContentType, request.Body))
        {
            using (SpiActivitySource.StartProxyActivity("dict.request.xml-sign"))
            {
                var unsigned = Encoding.UTF8.GetString(request.Body);
                var signed = await _hsmService.SignDictXmlAsync(unsigned, cancellationToken);
                outboundBody = Encoding.UTF8.GetBytes(signed);
            }
        }

        var forwardRequest = request with { Body = outboundBody };

        // 2. Forward to the real DICT API over mTLS.
        DictProxyResponse response;
        using (SpiActivitySource.StartProxyActivity("dict.forward"))
        {
            response = await _forwarder.SendAsync(forwardRequest, cancellationToken);
        }

        // 3. Re-sign the response body so System B sees a signature it trusts (RF-04 error responses
        //    included — StripSignatures + SignDictXmlAsync is a no-op-safe round trip on any XML).
        var inboundBody = response.Body;
        if (HasXmlBody(response.ContentType, response.Body))
        {
            using (SpiActivitySource.StartProxyActivity("dict.response.xml-sign"))
            {
                var unsigned = Encoding.UTF8.GetString(response.Body);
                var signed = await _hsmService.SignDictXmlAsync(unsigned, cancellationToken);
                inboundBody = Encoding.UTF8.GetBytes(signed);
            }
        }

        _metrics.RecordDictUpstreamStatus(response.StatusCode);
        _metrics.RecordDictProxyLatency(sw.Elapsed.TotalMilliseconds);
        _logger.LogDictProxied(request.Method, request.PathAndQuery, response.StatusCode);

        return response with { Body = inboundBody };
    }

    private static bool HasXmlBody(string? contentType, byte[] body) =>
        body.Length > 0
        && contentType is not null
        && contentType.Contains("xml", StringComparison.OrdinalIgnoreCase);
}

internal static partial class ProxyDictRequestUseCaseLogMessages
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "DICT request proxied. {Method} {PathAndQuery} -> {StatusCode}")]
    public static partial void LogDictProxied(this ILogger logger, string method, string pathAndQuery, int statusCode);
}
