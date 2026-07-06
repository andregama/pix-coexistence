using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace ConvivenciaPix.Application.UseCases.CorrelateMessages;

public sealed class CorrelateSystemBOutboundUseCase : ICorrelateSystemBOutboundUseCase
{
    private const string CorrelationEventsTopic = "spi.correlation.events";
    private const string ComparisonEventsTopic = "spi.comparison.events";

    private readonly ISpiSentMsgRepository _sentMsgRepo;
    private readonly ISpiXmlParser _xmlParser;
    private readonly IKafkaPublisher _publisher;
    private readonly ILogger<CorrelateSystemBOutboundUseCase> _logger;

    public CorrelateSystemBOutboundUseCase(
        ISpiSentMsgRepository sentMsgRepo,
        ISpiXmlParser xmlParser,
        IKafkaPublisher publisher,
        ILogger<CorrelateSystemBOutboundUseCase> logger)
    {
        _sentMsgRepo = sentMsgRepo;
        _xmlParser = xmlParser;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task ExecuteAsync(KafkaEnvelope envelope, IReadOnlySet<string> allowedTypes, CancellationToken ct)
    {
        var rawXml = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.PayloadBase64));
        var msgType = _xmlParser.ExtractMessageType(rawXml);

        if (!allowedTypes.Contains(msgType))
        {
            _logger.LogDebug("SystemB outbound: skipping unsupported MsgType={MsgType}", msgType);
            return;
        }

        var idempotentId = _xmlParser.ExtractIdempotentId(rawXml, msgType);
        var originalId = _xmlParser.ExtractOriginalIdempotentId(rawXml, msgType);
        string? msgId = null;
        try { msgId = _xmlParser.ExtractMessageId(rawXml); }
        catch { /* non-critical */ }

        // First-arrival create-if-missing: whichever of System A/B outbound arrives first creates
        // the shared row (keyed by IdempotentId); the second side updates it. Mirrors the
        // SpiReceivedMsg "first arrival creates the row" pattern.
        var msg = await _sentMsgRepo.FindByIdempotentIdAsync(idempotentId, ct);
        var isNew = msg is null;
        if (msg is null)
            msg = SpiSentMsg.Create(idempotentId, msgType);

        msg.UpdateFromSystemB(msgId, rawXml, null);
        if (originalId is not null)
            msg.SetOriginalMsgIdempotentId(originalId);

        if (isNew)
            await _sentMsgRepo.AddAsync(msg, ct);
        else
            await _sentMsgRepo.UpdateAsync(msg, ct);

        _logger.LogInformation(
            "SystemB outbound correlated. IdempotentId={Id} MsgType={Type} Created={Created}",
            idempotentId, msgType, isNew);

        if (msg.IsComplete)
            await PublishEventsAsync(msg, ct);
    }

    private async Task PublishEventsAsync(SpiSentMsg msg, CancellationToken ct)
    {
        var correlationEvent = new KafkaEnvelope(
            MessageId: msg.IdempotentId,
            PayloadBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes(msg.IdempotentId)),
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: msg.IdempotentId);

        await _publisher.PublishAsync(CorrelationEventsTopic, correlationEvent, ct);

        var comparisonEvent = new SpiComparisonEventDto(
            IdempotentId: msg.IdempotentId,
            MsgType: msg.MsgType,
            SystemAXml: msg.XmlMsgSystemA!,
            SystemBXml: msg.XmlMsgSystemB!,
            OccurredAt: DateTimeOffset.UtcNow);

        var comparisonEnvelope = new KafkaEnvelope(
            MessageId: msg.IdempotentId,
            PayloadBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(comparisonEvent))),
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: msg.IdempotentId);

        await _publisher.PublishAsync(ComparisonEventsTopic, comparisonEnvelope, ct);
    }
}
