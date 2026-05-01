using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.Mappers;
using ConvivenciaPix.Application.Tracing;
using ConvivenciaPix.Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ConvivenciaPix.Application.UseCases.PropagateResponse;

public sealed class PropagateResponseUseCase : IPropagateResponseUseCase
{
    private const int CorrelationRetries = 5;
    private static readonly TimeSpan CorrelationRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ResponseCacheTtl = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IResponseCache _responseCache;
    private readonly IHsmService _hsmService;
    private readonly IXmlSigningService _xmlSigningService;
    private readonly IKafkaPublisher _publisher;
    private readonly ISpiMetrics _metrics;
    private readonly ILogger<PropagateResponseUseCase> _logger;

    public PropagateResponseUseCase(
        IServiceScopeFactory scopeFactory,
        IResponseCache responseCache,
        IHsmService hsmService,
        IXmlSigningService xmlSigningService,
        IKafkaPublisher publisher,
        ISpiMetrics metrics,
        ILogger<PropagateResponseUseCase> logger)
    {
        _scopeFactory = scopeFactory;
        _responseCache = responseCache;
        _hsmService = hsmService;
        _xmlSigningService = xmlSigningService;
        _publisher = publisher;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task ExecuteAsync(string rawCdcJson, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var responseDto = SystemAOutboxMapper.MapV1(rawCdcJson);
        var idSystemA = responseDto.IdSystemA;

        // Retry loop to handle the race between correlate worker and proxy worker
        string? idSystemB = null;
        using (SpiActivitySource.StartProxyActivity("proxy.correlate-lookup"))
        {
            for (var attempt = 1; attempt <= CorrelationRetries; attempt++)
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var repo = scope.ServiceProvider.GetRequiredService<ISpiSentMsgRepository>();
                var sentMsg = await repo.FindByIdSystemAAsync(idSystemA, cancellationToken);

                if (sentMsg is not null)
                {
                    idSystemB = sentMsg.IdSystemB;
                    break;
                }

                if (attempt < CorrelationRetries)
                {
                    _logger.LogWarning("Correlation not yet available for IdSystemA={IdSystemA} (attempt {Attempt}/{Max}) — retrying", idSystemA, attempt, CorrelationRetries);
                    await Task.Delay(CorrelationRetryDelay, cancellationToken);
                }
            }
        }

        if (idSystemB is null)
            throw new InvalidOperationException(
                $"Correlation not found for IdSystemA={idSystemA} after {CorrelationRetries} retries. Routing to DLQ.");

        // Retrieve System B's original request for the comparison event
        var systemBXml = await _responseCache.GetAsync($"request:{idSystemB}", cancellationToken);
        if (systemBXml is null)
            _logger.LogWarning("System B request XML not found in Redis for IdSystemB={IdSystemB} — comparison event will be skipped", idSystemB);

        // Sign System A's XML response
        string signedXml;
        using (SpiActivitySource.StartProxyActivity("proxy.xml-sign"))
        {
            var cert = await _hsmService.GetSigningCertificateAsync(cancellationToken);
            signedXml = await _xmlSigningService.SignAsync(
                XDocument.Parse(responseDto.SignedXml), cert);
        }

        // Deposit signed response in Redis and signal waiters via Pub/Sub
        using (SpiActivitySource.StartProxyActivity("proxy.redis-deposit"))
        {
            await _responseCache.SetAsync(idSystemB, signedXml, ResponseCacheTtl, cancellationToken);
            await _responseCache.SignalResponseAsync(idSystemB, signedXml, cancellationToken);
        }

        _metrics.RecordProxyResponseLatency(sw.Elapsed.TotalMilliseconds);
        _logger.LogInformation("Signed response deposited in Redis. IdSystemA={IdSystemA} → IdSystemB={IdSystemB}", idSystemA, idSystemB);

        // Publish signed response to System B responses topic
        var responseEnvelope = new KafkaEnvelope(
            MessageId: responseDto.MessageId,
            PayloadBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes(signedXml)),
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: idSystemB);

        await _publisher.PublishAsync("spi.systemb.responses", responseEnvelope, cancellationToken);

        // Publish comparison event for the comparison engine
        if (systemBXml is not null)
        {
            await PublishComparisonEventAsync(
                idSystemA, idSystemB, responseDto.SignedXml, systemBXml, cancellationToken);
        }
    }

    private async Task PublishComparisonEventAsync(
        string idSystemA, string idSystemB,
        string systemAXml, string systemBXml,
        CancellationToken cancellationToken)
    {
        var comparisonEvent = new SpiComparisonEventDto(
            IdSystemA: idSystemA,
            IdSystemB: idSystemB,
            SystemAXml: systemAXml,
            SystemBXml: systemBXml,
            CorrelationSource: "Unknown",
            OccurredAt: DateTimeOffset.UtcNow);

        var envelope = new KafkaEnvelope(
            MessageId: Guid.NewGuid().ToString(),
            PayloadBase64: Convert.ToBase64String(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(comparisonEvent))),
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: idSystemA);

        await _publisher.PublishAsync("spi.comparison.events", envelope, cancellationToken);
    }
}
