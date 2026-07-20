using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.Mappers;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace ConvivenciaPix.Application.UseCases.CorrelateMessages;

public sealed class CorrelateSystemAInboundUseCase : ICorrelateSystemAInboundUseCase
{
    // Matches Topics.SystemBResponses; kept as a literal because the Application layer
    // does not reference Infrastructure (which owns the Topics registry).
    private const string SystemBResponsesTopic = "spi.systemb.responses";

    private readonly ISpiReceivedMsgRepository _receivedMsgRepo;
    private readonly ISpiSentMsgRepository _sentMsgRepo;
    private readonly ISpiXmlParser _xmlParser;
    private readonly IInboundResponseTransformer _transformer;
    private readonly IKafkaPublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly CorrelateInboundRetryOptions _retry;
    private readonly ILogger<CorrelateSystemAInboundUseCase> _logger;

    public CorrelateSystemAInboundUseCase(
        ISpiReceivedMsgRepository receivedMsgRepo,
        ISpiSentMsgRepository sentMsgRepo,
        ISpiXmlParser xmlParser,
        IInboundResponseTransformer transformer,
        IKafkaPublisher publisher,
        IOptions<CorrelateInboundRetryOptions> retryOptions,
        TimeProvider timeProvider,
        ILogger<CorrelateSystemAInboundUseCase> logger)
    {
        _receivedMsgRepo = receivedMsgRepo;
        _sentMsgRepo = sentMsgRepo;
        _xmlParser = xmlParser;
        _transformer = transformer;
        _publisher = publisher;
        _retry = retryOptions.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task ExecuteAsync(string rawCdcJson, IReadOnlySet<string> allowedTypes, CancellationToken ct)
    {
        var mapped = SystemAInboundMapper.Map(rawCdcJson);
        var msgType = _xmlParser.ExtractMessageType(mapped.XmlMsg);

        if (!allowedTypes.Contains(msgType))
        {
            _logger.LogDebug("SystemA inbound: skipping unsupported MsgType={MsgType}", msgType);
            return;
        }

        var idempotentId = _xmlParser.ExtractIdempotentId(mapped.XmlMsg, msgType);
        var originalId = _xmlParser.ExtractOriginalIdempotentId(mapped.XmlMsg, msgType);
        string? msgId = null;
        try { msgId = _xmlParser.ExtractMessageId(mapped.XmlMsg); }
        catch { /* non-critical */ }

        var existing = await _receivedMsgRepo.FindByIdempotentIdAsync(idempotentId, ct);
        if (existing is null)
        {
            var created = SpiReceivedMsg.CreateFromSystemA(
                idempotentId, msgType, msgId, mapped.XmlMsg, mapped.Problem, originalId);
            await _receivedMsgRepo.AddAsync(created, ct);
        }
        else
        {
            existing.SetMsgIdIfAbsent(msgId);
            existing.UpdateFromSystemA(mapped.XmlMsg, mapped.Problem);
            await _receivedMsgRepo.UpdateAsync(existing, ct);
        }

        // Correlate against the stored pacs.008 A/B pair so the response can be rewritten to the
        // values System B expects. The row only becomes complete once both outbound sides land; the
        // inbound response can race ahead of System B's side, so retry with backoff before giving up.
        // Persistently missing/incomplete correlation -> DLQ (via KafkaConsumerBase).
        var sent = await CorrelateSentMsgAsync(idempotentId, msgType, ct);

        var transformedXml = _transformer.Transform(mapped.XmlMsg, sent.XmlMsgSystemA!, sent.XmlMsgSystemB!);

        var readyDto = new SystemBInboundReadyDto(
            IdempotentId: idempotentId,
            MsgType: msgType,
            TransformedXml: transformedXml,
            OccurredAt: DateTimeOffset.UtcNow);

        var envelope = new KafkaEnvelope(
            MessageId: idempotentId,
            PayloadBase64: Convert.ToBase64String(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(readyDto))),
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: idempotentId);

        await _publisher.PublishAsync(SystemBResponsesTopic, envelope, ct);

        _logger.LogInformation(
            "SystemA inbound correlated and transformed for System B. IdempotentId={Id} MsgType={Type}",
            idempotentId, msgType);
    }

    /// <summary>
    /// Re-reads the correlated <c>SpiSentMsg</c> until it is complete, applying bounded exponential
    /// backoff to absorb the race where the inbound response arrives before System B's outbound side
    /// is persisted. Throws once <see cref="CorrelateInboundRetryOptions.MaxAttempts"/> is reached so
    /// the consumer dead-letters the event.
    /// </summary>
    private async Task<SpiSentMsg> CorrelateSentMsgAsync(string idempotentId, string msgType, CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, _retry.MaxAttempts);
        for (var attempt = 1; ; attempt++)
        {
            // FindByIdempotentIdAsync reads AsNoTracking, so each attempt observes newly-committed data.
            var sent = await _sentMsgRepo.FindByIdempotentIdAsync(idempotentId, ct);
            if (sent is not null && sent.IsComplete)
                return sent;

            if (attempt >= maxAttempts)
                throw new InvalidOperationException(
                    $"SpiSentMsg not found or incomplete for IdempotentId={idempotentId} MsgType={msgType} " +
                    $"after {attempt} attempt(s). Cannot transform response for System B. Routing to DLQ.");

            var delay = ComputeBackoff(attempt);
            _logger.LogWarning(
                "SystemA inbound: sent row not ready for IdempotentId={Id} MsgType={Type} " +
                "(attempt {Attempt}/{Max}); retrying in {DelayMs}ms.",
                idempotentId, msgType, attempt, maxAttempts, delay.TotalMilliseconds);
            await Task.Delay(delay, _timeProvider, ct);
        }
    }

    private TimeSpan ComputeBackoff(int attempt)
    {
        // attempt is 1-based; the first inter-attempt wait uses InitialDelayMs.
        var raw = _retry.InitialDelayMs * Math.Pow(_retry.BackoffMultiplier, attempt - 1);
        var capped = Math.Min(raw, _retry.MaxDelayMs);
        return TimeSpan.FromMilliseconds(Math.Max(0, capped));
    }
}
