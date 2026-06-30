using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.Mappers;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace ConvivenciaPix.Application.UseCases.CorrelateMessages;

public sealed class CorrelateSystemAOutboundUseCase : ICorrelateSystemAOutboundUseCase
{
    private const string CorrelationEventsTopic = "spi.correlation.events";
    private const string ComparisonEventsTopic = "spi.comparison.events";

    private readonly ISpiSentMsgRepository _sentMsgRepo;
    private readonly ISpiXmlParser _xmlParser;
    private readonly IKafkaPublisher _publisher;
    private readonly ILogger<CorrelateSystemAOutboundUseCase> _logger;

    public CorrelateSystemAOutboundUseCase(
        ISpiSentMsgRepository sentMsgRepo,
        ISpiXmlParser xmlParser,
        IKafkaPublisher publisher,
        ILogger<CorrelateSystemAOutboundUseCase> logger)
    {
        _sentMsgRepo = sentMsgRepo;
        _xmlParser = xmlParser;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task ExecuteAsync(string rawCdcJson, IReadOnlySet<string> allowedTypes, CancellationToken ct)
    {
        var mapped = SystemAOutboundMapper.Map(rawCdcJson);
        var msgType = _xmlParser.ExtractMessageType(mapped.XmlMsg);

        if (!allowedTypes.Contains(msgType))
        {
            _logger.LogDebug("SystemA outbound: skipping unsupported MsgType={MsgType}", msgType);
            return;
        }

        var idempotentId = _xmlParser.ExtractIdempotentId(mapped.XmlMsg, msgType);
        var originalId = _xmlParser.ExtractOriginalIdempotentId(mapped.XmlMsg, msgType);

        var msg = await _sentMsgRepo.FindByIdempotentIdAsync(idempotentId, ct);
        if (msg is null)
            throw new InvalidOperationException(
                $"SpiSentMsg not found for IdempotentId={idempotentId} MsgType={msgType}. Routing to DLQ.");

        msg.UpdateFromSystemA(mapped.MessageId, mapped.XmlMsg, mapped.Problem);
        if (originalId is not null)
            msg.SetOriginalMsgIdempotentId(originalId);

        await _sentMsgRepo.UpdateAsync(msg, ct);

        _logger.LogInformation(
            "SystemA outbound correlated. IdempotentId={Id} MsgType={Type}", idempotentId, msgType);

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
