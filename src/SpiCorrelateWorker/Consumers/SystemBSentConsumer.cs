using Confluent.Kafka;
using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using ConvivenciaPix.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace ConvivenciaPix.SpiCorrelateWorker.Consumers;

/// <summary>
/// Consumes System B requests from the proxy API and stores them as pending correlation records.
/// The matching against System A is done by SystemAResponseCorrelateConsumer when A's response arrives.
/// </summary>
public sealed class SystemBSentConsumer : KafkaConsumerBase<string, string>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISpiXmlParser _xmlParser;
    private readonly ILogger<SystemBSentConsumer> _logger;

    public SystemBSentConsumer(
        IConfiguration configuration,
        IProducer<string, string> dlqProducer,
        IServiceScopeFactory scopeFactory,
        ISpiXmlParser xmlParser,
        ILogger<SystemBSentConsumer> logger)
        : base(BuildConsumer(configuration), dlqProducer, Topics.SystemBRequests, logger)
    {
        _scopeFactory = scopeFactory;
        _xmlParser = xmlParser;
        _logger = logger;
    }

    protected override async Task ProcessMessageAsync(
        ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        var envelope = JsonSerializer.Deserialize<KafkaEnvelope>(result.Message.Value)
            ?? throw new InvalidOperationException("Failed to deserialize KafkaEnvelope from SystemBRequests");

        var rawXml = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.PayloadBase64));
        var idSystemB = envelope.CorrelationId
            ?? _xmlParser.ExtractEndToEndId(rawXml);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISpiPendingSystemBMsgRepository>();

        // Idempotency: skip if already stored
        if (await repo.ExistsByIdSystemBAsync(idSystemB, cancellationToken))
        {
            _logger.LogPendingAlreadyExists(idSystemB);
            return;
        }

        var timestamp = _xmlParser.ExtractTimestamp(rawXml);
        var amount = _xmlParser.ExtractAmount(rawXml);
        var payerId = _xmlParser.ExtractPayerId(rawXml);
        var payeeId = _xmlParser.ExtractPayeeId(rawXml);

        var pending = SpiPendingSystemBMsg.Create(
            idSystemB, envelope.MessageId, timestamp, amount, payerId, payeeId, rawXml);

        await repo.AddAsync(pending, cancellationToken);
        _logger.LogPendingStored(idSystemB, envelope.MessageId);
    }

    private static IConsumer<string, string> BuildConsumer(IConfiguration configuration) =>
        new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = configuration["Kafka:BootstrapServers"]
                ?? throw new InvalidOperationException("Kafka:BootstrapServers is required."),
            GroupId = "spi-correlate-systemb",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();
}

internal static partial class SystemBSentConsumerLogMessages
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Pending record already exists for IdSystemB={IdSystemB} — skipping")]
    public static partial void LogPendingAlreadyExists(this ILogger logger, string idSystemB);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stored pending System B message. IdSystemB={IdSystemB} MessageId={MessageId}")]
    public static partial void LogPendingStored(this ILogger logger, string idSystemB, string messageId);
}
