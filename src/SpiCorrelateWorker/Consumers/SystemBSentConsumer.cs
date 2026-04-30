using Confluent.Kafka;
using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.CorrelateMessages;
using ConvivenciaPix.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ConvivenciaPix.SpiCorrelateWorker.Consumers;

public sealed class SystemBSentConsumer : KafkaConsumerBase<string, string>
{
    private readonly IReceiveSystemBSentUseCase _useCase;

    public SystemBSentConsumer(
        IConfiguration configuration,
        IProducer<string, string> dlqProducer,
        IReceiveSystemBSentUseCase useCase,
        ISpiMetrics metrics,
        ILogger<SystemBSentConsumer> logger)
        : base(BuildConsumer(configuration), dlqProducer, Topics.SystemBRequests, logger, metrics)
    {
        _useCase = useCase;
    }

    protected override async Task ProcessMessageAsync(
        ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        var envelope = JsonSerializer.Deserialize<KafkaEnvelope>(result.Message.Value)
            ?? throw new InvalidOperationException("Failed to deserialize KafkaEnvelope from SystemBRequests");

        await _useCase.ExecuteAsync(envelope, cancellationToken);
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
