using ConvivenciaPix.Application.Common;
using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text;

namespace ConvivenciaPix.Application.UseCases.ReceiveSpiRequest;

public sealed class ReceiveSpiRequestUseCase : IReceiveSpiRequestUseCase
{
    private const string InboundTopic = "spi.systemb.requests";

    private readonly IKafkaPublisher _publisher;
    private readonly ISpiXmlParser _xmlParser;
    private readonly IResponseCache _cache;
    private readonly ILogger<ReceiveSpiRequestUseCase> _logger;

    public ReceiveSpiRequestUseCase(
        IKafkaPublisher publisher,
        ISpiXmlParser xmlParser,
        IResponseCache cache,
        ILogger<ReceiveSpiRequestUseCase> logger)
    {
        _publisher = publisher;
        _xmlParser = xmlParser;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> ExecuteAsync(
        IReadOnlyList<SpiInboundMessage> messages,
        string ispb,
        CancellationToken cancellationToken)
    {
        var resourceIds = new List<string>(messages.Count);
        var receivedAt = DateTimeOffset.UtcNow;

        foreach (var message in messages)
        {
            var piResourceId = ResourceIdGenerator.Generate();
            resourceIds.Add(piResourceId);

            // Best-effort MsgId/IdempotentId extraction for correlation. Bacen explicitly does
            // not validate at this stage, so parse failures must not reject the message.
            // The idempotency key is message-type dependent (pacs.008/002/004), so resolve the
            // type first and let the parser pick the right field.
            string? messageId = null;
            string? idSystemB = null;
            try
            {
                messageId = _xmlParser.ExtractMessageId(message.RawXml);
                var msgType = _xmlParser.ExtractMessageType(message.RawXml);
                idSystemB = _xmlParser.ExtractIdempotentId(message.RawXml, msgType);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                _logger.LogWarning(
                    "Inbound XML could not be parsed for ids (still enqueued for processing). PiResourceId={PiResourceId}: {Error}",
                    piResourceId, ex.Message);
            }

            if (idSystemB is not null)
            {
                // Preserve the raw XML for the comparison engine.
                await _cache.SetAsync(
                    $"request:{idSystemB}", message.RawXml,
                    TimeSpan.FromHours(1), cancellationToken);
            }

            var envelope = new KafkaEnvelope(
                MessageId: messageId ?? piResourceId,
                PayloadBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes(message.RawXml)),
                Timestamp: receivedAt,
                CorrelationId: idSystemB,
                Ispb: ispb,
                PiResourceId: piResourceId);

            await _publisher.PublishAsync(InboundTopic, envelope, cancellationToken);

            _logger.LogInformation(
                "SPI inbound stored. Ispb={Ispb} PiResourceId={PiResourceId} MessageId={MessageId} IdSystemB={IdSystemB}",
                ispb, piResourceId, messageId, idSystemB);
        }

        return resourceIds;
    }
}
