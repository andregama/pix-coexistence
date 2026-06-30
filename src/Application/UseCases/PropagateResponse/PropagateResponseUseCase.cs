using ConvivenciaPix.Application.Common;
using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.Mappers;
using ConvivenciaPix.Application.Tracing;
using ConvivenciaPix.Domain.Entities;
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
    private const string ComparisonEventsTopic = "spi.comparison.events";
    private const string CorrelationEventsTopic = "spi.correlation.events";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOutboundStream _outboundStream;
    private readonly IHsmService _hsmService;
    private readonly IXmlSigningService _xmlSigningService;
    private readonly IKafkaPublisher _publisher;
    private readonly ISpiXmlParser _xmlParser;
    private readonly ISpiMetrics _metrics;
    private readonly ILogger<PropagateResponseUseCase> _logger;

    public PropagateResponseUseCase(
        IServiceScopeFactory scopeFactory,
        IOutboundStream outboundStream,
        IHsmService hsmService,
        IXmlSigningService xmlSigningService,
        IKafkaPublisher publisher,
        ISpiXmlParser xmlParser,
        ISpiMetrics metrics,
        ILogger<PropagateResponseUseCase> logger)
    {
        _scopeFactory = scopeFactory;
        _outboundStream = outboundStream;
        _hsmService = hsmService;
        _xmlSigningService = xmlSigningService;
        _publisher = publisher;
        _xmlParser = xmlParser;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task ExecuteAsync(string rawCdcJson, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var mapped = SystemAInboundMapper.Map(rawCdcJson);
        var msgType = _xmlParser.ExtractMessageType(mapped.XmlMsg);
        var idempotentId = _xmlParser.ExtractIdempotentId(mapped.XmlMsg, msgType);

        // Sign System A's XML so it can be delivered to System B
        string signedXml;
        using (SpiActivitySource.StartProxyActivity("proxy.xml-sign"))
        {
            var cert = await _hsmService.GetSigningCertificateAsync(cancellationToken);
            signedXml = await _xmlSigningService.SignAsync(
                XDocument.Parse(mapped.XmlMsg), cert);
        }

        // Enqueue signed XML onto the outbound stream for System B to pull
        var piResourceId = ResourceIdGenerator.Generate();
        using (SpiActivitySource.StartProxyActivity("proxy.outbound-enqueue"))
        {
            await _outboundStream.EnqueueAsync(piResourceId, signedXml, cancellationToken);
        }

        // Upsert SpiReceivedMsg with the signed XML (handles race with CorrelateSystemAInboundUseCase)
        SpiReceivedMsg msg;
        using (SpiActivitySource.StartProxyActivity("proxy.db-upsert"))
        {
            msg = await UpsertReceivedMsgAsync(idempotentId, msgType, mapped, signedXml, cancellationToken);
        }

        _metrics.RecordProxyResponseLatency(sw.Elapsed.TotalMilliseconds);
        _logger.LogInformation(
            "Signed response enqueued. IdempotentId={IdempotentId} MsgType={MsgType} PiResourceId={PiResourceId}",
            idempotentId, msgType, piResourceId);

        if (msg.IsComplete)
            await PublishEventsAsync(msg, cancellationToken);
    }

    private async Task<SpiReceivedMsg> UpsertReceivedMsgAsync(
        string idempotentId, string msgType,
        SystemAInboundMapper.Result mapped, string signedXml,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISpiReceivedMsgRepository>();

        var existing = await repo.FindByIdempotentIdAsync(idempotentId, ct);
        if (existing is not null)
        {
            existing.SetSystemBXml(signedXml);
            await repo.UpdateAsync(existing, ct);
            return existing;
        }

        var originalId = _xmlParser.ExtractOriginalIdempotentId(mapped.XmlMsg, msgType);
        var created = SpiReceivedMsg.CreateFromSystemB(
            idempotentId, msgType, msgId: null, signedXml, errorCode: null, originalId);
        await repo.AddAsync(created, ct);
        return created;
    }

    private async Task PublishEventsAsync(SpiReceivedMsg msg, CancellationToken ct)
    {
        var correlationEnvelope = new KafkaEnvelope(
            MessageId: msg.IdempotentId,
            PayloadBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes(msg.IdempotentId)),
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: msg.IdempotentId);

        await _publisher.PublishAsync(CorrelationEventsTopic, correlationEnvelope, ct);

        var comparisonEvent = new SpiComparisonEventDto(
            IdempotentId: msg.IdempotentId,
            MsgType: msg.MsgType,
            SystemAXml: msg.XmlMsgSystemA!,
            SystemBXml: msg.XmlMsgSystemB!,
            OccurredAt: DateTimeOffset.UtcNow);

        var comparisonEnvelope = new KafkaEnvelope(
            MessageId: msg.IdempotentId,
            PayloadBase64: Convert.ToBase64String(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(comparisonEvent))),
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: msg.IdempotentId);

        await _publisher.PublishAsync(ComparisonEventsTopic, comparisonEnvelope, ct);
    }
}
