using Confluent.Kafka;
using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using ConvivenciaPix.Domain.ValueObjects;
using ConvivenciaPix.Infrastructure.Messaging;
using ConvivenciaPix.Infrastructure.Messaging.Debezium;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace ConvivenciaPix.SpiCorrelateWorker.Consumers;

/// <summary>
/// Consumes System A CDC responses and correlates them to System B pending records.
/// Primary strategy: orchestrator query. Fallback: heuristic match by timestamp/amount/payer/payee.
/// CorrelationSource is stored and logged for strategy accuracy monitoring.
/// </summary>
public sealed class SystemAResponseCorrelateConsumer : KafkaConsumerBase<string, string>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOrchestratorClient _orchestratorClient;
    private readonly ISpiXmlParser _xmlParser;
    private readonly IKafkaPublisher _publisher;
    private readonly int _heuristicWindowSeconds;
    private readonly ILogger<SystemAResponseCorrelateConsumer> _logger;

    public SystemAResponseCorrelateConsumer(
        IConfiguration configuration,
        IProducer<string, string> dlqProducer,
        IServiceScopeFactory scopeFactory,
        IOrchestratorClient orchestratorClient,
        ISpiXmlParser xmlParser,
        IKafkaPublisher publisher,
        ILogger<SystemAResponseCorrelateConsumer> logger)
        : base(BuildConsumer(configuration), dlqProducer, Topics.SystemAResponses, logger)
    {
        _scopeFactory = scopeFactory;
        _orchestratorClient = orchestratorClient;
        _xmlParser = xmlParser;
        _publisher = publisher;
        _heuristicWindowSeconds = configuration.GetValue("Correlate:HeuristicWindowSeconds", 60);
        _logger = logger;
    }

    protected override async Task ProcessMessageAsync(
        ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        var responseDto = SystemAOutboxMapper.MapV1(result.Message.Value);
        var idSystemA = responseDto.IdSystemA;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sentRepo = scope.ServiceProvider.GetRequiredService<ISpiSentMsgRepository>();
        var pendingRepo = scope.ServiceProvider.GetRequiredService<ISpiPendingSystemBMsgRepository>();

        // Skip if already correlated (idempotency)
        var existing = await sentRepo.FindByIdSystemAAsync(idSystemA, cancellationToken);
        if (existing is not null)
        {
            _logger.LogAlreadyCorrelated(idSystemA);
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
            // Throw to trigger DLQ routing via the base class; the message can be
            // replayed manually after the pending record is confirmed to exist.
            throw new InvalidOperationException(
                $"No correlation match found for IdSystemA={idSystemA}. " +
                $"Timestamp={timestamp:O} Amount={amount} PayerId={payerId} PayeeId={payeeId}");
        }

        _logger.LogHeuristicMatch(idSystemA, match.IdSystemB, timestamp, amount);
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
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        IdSystemA = idSystemA,
                        IdSystemB = idSystemB,
                        CorrelationSource = source.Value,
                        CorrelatedAt = DateTimeOffset.UtcNow
                    }))),
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: idSystemA);

        await _publisher.PublishAsync(Topics.CorrelationEvents, correlationEvent, cancellationToken);

        _logger.LogCorrelated(idSystemA, idSystemB, source.Value);
    }

    private static IConsumer<string, string> BuildConsumer(IConfiguration configuration) =>
        new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = configuration["Kafka:BootstrapServers"]
                ?? throw new InvalidOperationException("Kafka:BootstrapServers is required."),
            GroupId = "spi-correlate-systema",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();
}

internal static partial class SystemAResponseCorrelateConsumerLogMessages
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Correlation already exists for IdSystemA={IdSystemA} — skipping")]
    public static partial void LogAlreadyCorrelated(this ILogger logger, string idSystemA);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Heuristic match found for IdSystemA={IdSystemA} → IdSystemB={IdSystemB}. Timestamp={Timestamp:O} Amount={Amount}")]
    public static partial void LogHeuristicMatch(this ILogger logger, string idSystemA, string idSystemB,
        DateTimeOffset timestamp, decimal amount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Correlation saved. IdSystemA={IdSystemA} → IdSystemB={IdSystemB} Source={Source}")]
    public static partial void LogCorrelated(this ILogger logger, string idSystemA, string idSystemB, string source);
}
