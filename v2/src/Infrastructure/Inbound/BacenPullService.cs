using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Application.UseCases.ProcessInboundMessage;
using ConvivenciaPix.V2.Infrastructure.Parsing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace ConvivenciaPix.V2.Infrastructure.Inbound;

public sealed class InboundPullOptions
{
    /// <summary>
    /// Master switch. The proxy must be the only consumer of the bank's SPI output stream: enable
    /// it only once System A no longer pulls from Bacen directly.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>ISPB of the bank, as used in the SPI URLs.</summary>
    public string Ispb { get; set; } = string.Empty;

    /// <summary>Number of parallel pull streams opened against the SPI.</summary>
    public int Streams { get; set; } = 1;

    /// <summary>Must exceed the SPI's long-poll wait.</summary>
    public int PullTimeoutSeconds { get; set; } = 60;

    public int ErrorBackoffMs { get; set; } = 2000;
}

/// <summary>
/// The only consumer of the bank's SPI output stream. Each loop pulls a batch, stores every message
/// (with its routing decision) and only then follows PI-Pull-Next — which is what acknowledges the
/// batch to Bacen. If anything fails before that, the batch is not acknowledged and Bacen delivers
/// it again; messages already stored are recognised by their PI-ResourceId.
/// </summary>
public sealed class BacenPullService : BackgroundService
{
    private static readonly IReadOnlyDictionary<string, string> PullHeaders =
        new Dictionary<string, string> { ["Accept"] = "multipart/mixed, application/xml" };

    private readonly ISpiGateway _gateway;
    private readonly IProcessInboundMessageUseCase _processMessage;
    private readonly InboundPullOptions _options;
    private readonly ILogger<BacenPullService> _logger;

    public BacenPullService(
        ISpiGateway gateway,
        IProcessInboundMessageUseCase processMessage,
        IOptions<InboundPullOptions> options,
        ILogger<BacenPullService> logger)
    {
        _gateway = gateway;
        _processMessage = processMessage;
        _options = options.Value;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogPullDisabled();
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(_options.Ispb))
            throw new InvalidOperationException("InboundPull:Ispb is required when InboundPull:Enabled is true.");

        var streams = Enumerable.Range(1, Math.Max(1, _options.Streams))
            .Select(index => Task.Run(() => RunStreamAsync(index, stoppingToken), CancellationToken.None));
        return Task.WhenAll(streams);
    }

    private async Task RunStreamAsync(int index, CancellationToken stoppingToken)
    {
        var startPath = $"/api/v1/out/{_options.Ispb}/stream/start";
        var path = startPath;
        _logger.LogStreamStarted(index);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await _gateway.SendAsync(
                    new SpiGatewayRequest("GET", path, PullHeaders, null, [], _options.PullTimeoutSeconds, IsPull: true),
                    stoppingToken);

                if (response.StatusCode == 200)
                {
                    foreach (var message in await ReadMessagesAsync(response, stoppingToken))
                        await _processMessage.ExecuteAsync(message.PiResourceId!, message.Body, stoppingToken);
                }
                else if (response.StatusCode != 204)
                {
                    _logger.LogUnexpectedStatus(index, path, response.StatusCode);
                    path = startPath;
                    await BackoffAsync(stoppingToken);
                    continue;
                }

                // Everything in the batch is stored: moving to the next URL confirms it to Bacen.
                path = NextPath(response.Header("PI-Pull-Next")) ?? startPath;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never retry the same pull URL: restart the stream and let Bacen redeliver.
                _logger.LogPullFailed(ex, index, path);
                path = startPath;
                await BackoffAsync(stoppingToken);
            }
        }

        await CloseStreamAsync(index, path, startPath);
    }

    private static async Task<IReadOnlyList<SpiMessagePart>> ReadMessagesAsync(
        SpiGatewayResponse response, CancellationToken cancellationToken)
    {
        if (SpiMultipart.IsMultipart(response.ContentType))
        {
            var parts = await SpiMultipart.ReadPartsAsync(
                    response.Body, response.ContentType, int.MaxValue, cancellationToken)
                ?? throw new InvalidDataException("The SPI returned a multipart body that could not be read.");
            return parts.Select(p => p with { PiResourceId = p.PiResourceId ?? FallbackId(p.Body) }).ToList();
        }

        if (response.Body.Length == 0)
            return [];

        var resourceId = response.Header("PI-ResourceId");
        return [new SpiMessagePart(response.Body, string.IsNullOrWhiteSpace(resourceId) ? FallbackId(response.Body) : resourceId.Trim())];
    }

    // A message without PI-ResourceId still needs a stable idempotency key: derive it from the content.
    private static string FallbackId(byte[] body) => "sha-" + Convert.ToHexString(SHA256.HashData(body))[..60];

    private static string? NextPath(string? pullNext)
    {
        if (string.IsNullOrWhiteSpace(pullNext)) return null;
        return Uri.TryCreate(pullNext, UriKind.Absolute, out var absolute) && absolute.Scheme.StartsWith("http", StringComparison.Ordinal)
            ? absolute.PathAndQuery
            : pullNext.Trim();
    }

    // On shutdown the last batch is stored but not yet confirmed; DELETE on the pending URL confirms
    // it and closes the stream. Best effort — an unconfirmed batch is redelivered and deduplicated.
    private async Task CloseStreamAsync(int index, string path, string startPath)
    {
        if (path == startPath) return;
        try
        {
            await _gateway.SendAsync(
                new SpiGatewayRequest("DELETE", path, PullHeaders, null, [], 10, IsPull: true), CancellationToken.None);
            _logger.LogStreamClosed(index);
        }
        catch (Exception ex)
        {
            _logger.LogStreamCloseFailed(ex, index);
        }
    }

    private async Task BackoffAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Math.Max(100, _options.ErrorBackoffMs), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }
}

internal static partial class BacenPullLogMessages
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbound pull is disabled (InboundPull:Enabled=false); no message is being pulled from the SPI")]
    public static partial void LogPullDisabled(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "SPI pull stream {Index} started")]
    public static partial void LogStreamStarted(this ILogger logger, int index);

    [LoggerMessage(Level = LogLevel.Information, Message = "SPI pull stream {Index} closed")]
    public static partial void LogStreamClosed(this ILogger logger, int index);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SPI pull stream {Index} could not be closed cleanly")]
    public static partial void LogStreamCloseFailed(this ILogger logger, Exception ex, int index);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SPI pull stream {Index}: GET {Path} returned {StatusCode}; restarting the stream")]
    public static partial void LogUnexpectedStatus(this ILogger logger, int index, string path, int statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "SPI pull stream {Index} failed on {Path}; batch not acknowledged, restarting the stream")]
    public static partial void LogPullFailed(this ILogger logger, Exception ex, int index, string path);
}
