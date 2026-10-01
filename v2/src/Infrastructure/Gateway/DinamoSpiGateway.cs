using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Infrastructure.Hsm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace ConvivenciaPix.V2.Infrastructure.Gateway;

/// <summary>
/// Staging/Production <see cref="ISpiGateway"/>: the mTLS request to the SPI is performed by the HSM
/// through <see cref="IDinamoSdkClient.SendPix"/> — the bank's client certificate never leaves it.
///
/// SendPix blocks for the whole request (and a pull is a long-poll), so each call runs on a
/// dedicated thread instead of starving the thread pool, and forwards are capped by a semaphore so
/// a slow SPI cannot exhaust threads or HSM sessions. Nothing is retried here.
/// </summary>
public sealed class DinamoSpiGateway : ISpiGateway, IDisposable
{
    private readonly IDinamoSdkClient _sdk;
    private readonly SpiGatewayOptions _options;
    private readonly SemaphoreSlim _forwardSlots;
    private readonly ILogger<DinamoSpiGateway> _logger;

    public DinamoSpiGateway(IDinamoSdkClient sdk, IOptions<SpiGatewayOptions> options, ILogger<DinamoSpiGateway> logger)
    {
        _sdk = sdk;
        _options = options.Value;
        _forwardSlots = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentForwards));
        _logger = logger;
    }

    public async Task<SpiGatewayResponse> SendAsync(SpiGatewayRequest request, CancellationToken cancellationToken = default)
    {
        var method = request.Method.ToUpperInvariant() switch
        {
            "GET" => PixHttpMethod.Get,
            "POST" => PixHttpMethod.Post,
            "PUT" => PixHttpMethod.Put,
            "DELETE" => PixHttpMethod.Delete,
            _ => throw new NotSupportedException($"The SPI gateway does not support HTTP method '{request.Method}'.")
        };
        var url = SpiGatewayUrl.Build(_options.BaseUrl, request.PathAndQuery);

        // Dinamo expects "Name: Value" entries. Content-Type is the only Content-* header we set;
        // the SDK adds the encoding headers itself when UseGzip is on.
        var headers = request.Headers.Select(h => $"{h.Key}: {h.Value}").ToList();
        if (request.Body.Length > 0 && !string.IsNullOrEmpty(request.ContentType))
            headers.Add($"Content-Type: {request.ContentType}");

        // Pull streams are few and fixed in number, so they do not take a forward slot.
        var limited = !request.IsPull;
        if (limited)
            await _forwardSlots.WaitAsync(cancellationToken);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await Task.Factory.StartNew(
                () => _sdk.SendPix(
                    method,
                    _options.MtlsKeyId,
                    _options.MtlsCertId,
                    _options.ServerCertChainId,
                    url,
                    headers,
                    request.Body,
                    request.TimeoutSeconds,
                    _options.UseGzip,
                    _options.VerifyHostName),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            _logger.LogSpiResponse(request.Method, request.PathAndQuery, response.StatusCode);
            return new SpiGatewayResponse(
                response.StatusCode,
                response.Headers.ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase),
                response.ContentType,
                response.Body);
        }
        catch (Exception ex) when (
            !cancellationToken.IsCancellationRequested
            && (ex is OperationCanceledException
                || Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(request.TimeoutSeconds)))
        {
            // The HSM SDK reports a timeout as a generic failure; the elapsed time tells them apart.
            throw new SpiGatewayTimeoutException(
                $"The SPI did not answer {request.Method} {request.PathAndQuery} within {request.TimeoutSeconds}s.", ex);
        }
        finally
        {
            if (limited)
                _forwardSlots.Release();
        }
    }

    public void Dispose() => _forwardSlots.Dispose();
}
