using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.Mappers;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using ConvivenciaPix.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ConvivenciaPix.Application.UseCases.CorrelateMessages;

public sealed class CorrelateMessagesUseCase : ICorrelateMessagesUseCase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOrchestratorClient _orchestratorClient;
    private readonly ISpiXmlParser _xmlParser;
    private readonly IKafkaPublisher _publisher;
    private readonly ISpiMetrics _metrics;
    private readonly int _heuristicWindowSeconds;
    private readonly ILogger<CorrelateMessagesUseCase> _logger;

    public CorrelateMessagesUseCase(
        IServiceScopeFactory scopeFactory,
        IOrchestratorClient orchestratorClient,
        ISpiXmlParser xmlParser,
        IKafkaPublisher publisher,
        ISpiMetrics metrics,
        IConfiguration configuration,
        ILogger<CorrelateMessagesUseCase> logger)
    {
        _scopeFactory = scopeFactory;
        _orchestratorClient = orchestratorClient;
        _xmlParser = xmlParser;
        _publisher = publisher;
        _metrics = metrics;
        _heuristicWindowSeconds = configuration.GetValue("Correlate:HeuristicWindowSeconds", 60);
        _logger = logger;
    }

    public async Task ExecuteAsync(string rawCdcJson, CancellationToken cancellationToken)
    {
        var responseDto = SystemAOutboxMapper.MapV1(rawCdcJson);
        var idSystemA = responseDto.IdSystemA;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sentRepo = scope.ServiceProvider.GetRequiredService<ISpiSentMsgRepository>();
        var pendingRepo = scope.ServiceProvider.GetRequiredService<ISpiPendingSystemBMsgRepository>();

        // Skip if already correlated (idempotency)
        var existing = await sentRepo.FindByIdSystemAAsync(idSystemA, cancellationToken);
        if (existing is not null)
        {
            _logger.LogDebug("Correlation already exists for IdSystemA={IdSystemA} — skipping", idSystemA);
            return;
        }

        // --- Primary strategy: orchestrator ---
        var orchestratorResult = await _orchestratorClient.FindCorrelationAsync(idSystemA, cancellationToken);
        if (orchestratorResult is not null)
        {
            await SaveAndPublishAsync(sentRepo, pendingRepo, idSystemA,
                orchestratorResult.IdSystemB, CorrelationSource.Orchestrator,
                matchedPendingId: null, cancellationToken);
            return;
        }

        // --- Fallback strategy: heuristic ---
        var timestamp = _xmlParser.ExtractTimestamp(responseDto.SignedXml);
        var amount = _xmlParser.ExtractAmount(responseDto.SignedXml);
        var payerId = _xmlParser.ExtractPayerId(responseDto.SignedXml);
        var payeeId = _xmlParser.ExtractPayeeId(responseDto.SignedXml);

        var match = await pendingRepo.FindHeuristicMatchAsync(
            timestamp, amount, payerId, payeeId, _heuristicWindowSeconds, cancellationToken);

        if (match is null)
        {
            // No match yet — the SystemB pending record may not have arrived yet.
            // Throw to trigger DLQ routing via the base consumer class.
            throw new InvalidOperationException(
                $"No correlation match found for IdSystemA={idSystemA}. " +
                $"Timestamp={timestamp:O} Amount={amount} PayerId={payerId} PayeeId={payeeId}");
        }

        _logger.LogInformation("Heuristic match found for IdSystemA={IdSystemA} → IdSystemB={IdSystemB}. Timestamp={Timestamp:O} Amount={Amount}",
            idSystemA, match.IdSystemB, timestamp, amount);

        await SaveAndPublishAsync(sentRepo, pendingRepo, idSystemA,
            match.IdSystemB, CorrelationSource.Heuristic,
            matchedPendingId: match.Id, cancellationToken);
    }

    private async Task SaveAndPublishAsync(
        ISpiSentMsgRepository sentRepo,
        ISpiPendingSystemBMsgRepository pendingRepo,
        string idSystemA, string idSystemB,
        CorrelationSource source,
        Guid? matchedPendingId,
        CancellationToken cancellationToken)
    {
        var sentMsg = SpiSentMsg.Create(idSystemA, idSystemB, source);
        await sentRepo.AddAsync(sentMsg, cancellationToken);

        if (matchedPendingId.HasValue)
            await pendingRepo.DeleteAsync(matchedPendingId.Value, cancellationToken);

        var correlationEvent = new KafkaEnvelope(
            MessageId: Guid.NewGuid().ToString(),
            PayloadBase64: Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new
                    {
                        IdSystemA = idSystemA,
                        IdSystemB = idSystemB,
                        CorrelationSource = source.Value,
                        CorrelatedAt = DateTimeOffset.UtcNow
                    }))),
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: idSystemA);

        await _publisher.PublishAsync("spi.correlation.events", correlationEvent, cancellationToken);

        _metrics.RecordCorrelationSource(source.Value);
        _logger.LogInformation("Correlation saved. IdSystemA={IdSystemA} → IdSystemB={IdSystemB} Source={Source}",
            idSystemA, idSystemB, source.Value);
    }
}
