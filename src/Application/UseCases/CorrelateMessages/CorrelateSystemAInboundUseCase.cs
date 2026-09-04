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
    private readonly ISpiMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly CorrelateInboundRetryOptions _retry;
    private readonly ILogger<CorrelateSystemAInboundUseCase> _logger;

    public CorrelateSystemAInboundUseCase(
        ISpiReceivedMsgRepository receivedMsgRepo,
        ISpiSentMsgRepository sentMsgRepo,
        ISpiXmlParser xmlParser,
        IInboundResponseTransformer transformer,
        IKafkaPublisher publisher,
        ISpiMetrics metrics,
        IOptions<CorrelateInboundRetryOptions> retryOptions,
        TimeProvider timeProvider,
        ILogger<CorrelateSystemAInboundUseCase> logger)
    {
        _receivedMsgRepo = receivedMsgRepo;
        _sentMsgRepo = sentMsgRepo;
        _xmlParser = xmlParser;
        _transformer = transformer;
        _publisher = publisher;
        _metrics = metrics;
        _retry = retryOptions.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task ExecuteAsync(
        string rawCdcJson,
        IReadOnlySet<string> allowedTypes,
        IReadOnlySet<string> primaryTypes,
        IReadOnlySet<string> correlateByOriginalMsgIdTypes,
        IReadOnlySet<string> correlateByOriginalEndToEndIdTypes,
        CancellationToken ct)
    {
        var mapped = SystemAInboundMapper.Map(rawCdcJson);
        var msgType = _xmlParser.ExtractMessageType(mapped.XmlMsg);

        if (!allowedTypes.Contains(msgType))
        {
            _logger.LogDebug("SystemA inbound: skipping unsupported MsgType={MsgType}", msgType);
            return;
        }

        var idempotentId = _xmlParser.ExtractCorrelationKey(mapped.XmlMsg, msgType);
        var originalId = _xmlParser.ExtractOriginalIdempotentId(mapped.XmlMsg, msgType);
        var correlationSource = _xmlParser.GetCorrelationSource(msgType);
        string? msgId = null;
        try { msgId = _xmlParser.ExtractMessageId(mapped.XmlMsg); }
        catch { /* non-critical */ }

        var existing = await _receivedMsgRepo.FindByIdempotentIdAsync(idempotentId, ct);
        if (existing is null)
        {
            var created = SpiReceivedMsg.CreateFromSystemA(
                idempotentId, msgType, msgId, mapped.XmlMsg, mapped.Problem, originalId);
            created.SetCorrelationSource(correlationSource);
            _metrics.RecordCorrelationSource(correlationSource);
            await _receivedMsgRepo.AddAsync(created, ct);
        }
        else
        {
            existing.SetMsgIdIfAbsent(msgId);
            existing.UpdateFromSystemA(mapped.XmlMsg, mapped.Problem);
            await _receivedMsgRepo.UpdateAsync(existing, ct);
        }

        // Primary/unsolicited inbound initiations (e.g. a received pacs.008 Pix credit) answer no
        // A/B outbound message, so there is no SpiSentMsg to correlate and nothing to rewrite. Pass
        // them through to System B unchanged — the proxy worker signs and delivers them like any
        // other System B inbound, so B also processes the incoming transaction.
        if (primaryTypes.Contains(msgType))
        {
            await PublishReadyForSystemBAsync(idempotentId, msgType, mapped.XmlMsg, ct);
            _logger.LogInformation(
                "SystemA inbound primary message passed through to System B. IdempotentId={Id} MsgType={Type}",
                idempotentId, msgType);
            return;
        }

        // Message-level responses (e.g. admi.002 rejections) reference the original message by its
        // MsgId, not by a transaction/correlation key. Correlate via SpiSentMsg.MsgIdSystemA; when
        // not found the message is still replicated to System B (unchanged) with a warning, not DLQ'd.
        if (correlateByOriginalMsgIdTypes.Contains(msgType))
        {
            var byMsgId = string.IsNullOrEmpty(originalId)
                ? null
                : await _sentMsgRepo.FindByMsgIdSystemAAsync(originalId, ct);
            await PublishCorrelatedOrReplicateAsync(idempotentId, msgType, mapped.XmlMsg, originalId, byMsgId, ct);
            return;
        }

        // Responses that reference the original transfer by OrgnlEndToEndId (e.g. pacs.004 returns).
        // Correlate to the original transfer via SpiSentMsg.IdempotentId. Returns can arrive up to
        // 90 days later while SpiSentMsg has a 30-day TTL, so when the original is gone the pacs.004 is
        // still replicated to System B (unchanged) with a warning, not DLQ'd. Note: idempotentId is the
        // return's own id (RtrId), so multiple returns for one original each get their own row.
        if (correlateByOriginalEndToEndIdTypes.Contains(msgType))
        {
            var byOriginal = string.IsNullOrEmpty(originalId)
                ? null
                : await _sentMsgRepo.FindByIdempotentIdAsync(originalId, ct);
            await PublishCorrelatedOrReplicateAsync(idempotentId, msgType, mapped.XmlMsg, originalId, byOriginal, ct);
            return;
        }

        // Correlate against the stored pacs.008 A/B pair so the response can be rewritten to the
        // values System B expects. The row only becomes complete once both outbound sides land; the
        // inbound response can race ahead of System B's side, so retry with backoff before giving up.
        // Persistently missing/incomplete correlation -> DLQ (via KafkaConsumerBase).
        var sent = await CorrelateSentMsgAsync(idempotentId, msgType, ct);

        var transformedXml = _transformer.Transform(mapped.XmlMsg, sent.XmlMsgSystemA!, sent.XmlMsgSystemB!);

        await PublishReadyForSystemBAsync(idempotentId, msgType, transformedXml, ct);

        _logger.LogInformation(
            "SystemA inbound correlated and transformed for System B. IdempotentId={Id} MsgType={Type}",
            idempotentId, msgType);
    }

    /// <summary>
    /// Resilient correlation for responses that reference an original message/transfer: when the
    /// original (<paramref name="sent"/>) is found and complete, transform the message for System B;
    /// otherwise replicate it unchanged and warn. Never dead-letters.
    /// </summary>
    private async Task PublishCorrelatedOrReplicateAsync(
        string idempotentId, string msgType, string xmlMsg, string? originalRef, SpiSentMsg? sent, CancellationToken ct)
    {
        string xmlForSystemB;
        if (sent is not null && sent.IsComplete)
        {
            xmlForSystemB = _transformer.Transform(xmlMsg, sent.XmlMsgSystemA!, sent.XmlMsgSystemB!);
            _logger.LogInformation(
                "SystemA inbound {Type} correlated to original ref={OrigRef} and transformed for System B. IdempotentId={Id}",
                msgType, originalRef, idempotentId);
        }
        else
        {
            xmlForSystemB = xmlMsg;
            _logger.LogWarning(
                "SystemA inbound {Type}: original ref={OrigRef} not found or incomplete in SpiSentMsg; " +
                "replicating to System B without correlation. IdempotentId={Id}",
                msgType, originalRef, idempotentId);
        }

        await PublishReadyForSystemBAsync(idempotentId, msgType, xmlForSystemB, ct);
    }

    /// <summary>Publishes the ready-for-System-B event (signed + delivered by the proxy worker).</summary>
    private Task PublishReadyForSystemBAsync(string idempotentId, string msgType, string xmlForSystemB, CancellationToken ct)
    {
        var readyDto = new SystemBInboundReadyDto(
            IdempotentId: idempotentId,
            MsgType: msgType,
            TransformedXml: xmlForSystemB,
            OccurredAt: DateTimeOffset.UtcNow);

        var envelope = new KafkaEnvelope(
            MessageId: idempotentId,
            PayloadBase64: Convert.ToBase64String(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(readyDto))),
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: idempotentId);

        return _publisher.PublishAsync(SystemBResponsesTopic, envelope, ct);
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
