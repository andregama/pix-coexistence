using Confluent.Kafka;
using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.CorrelateMessages;
using Microsoft.Extensions.DependencyInjection;
using ConvivenciaPix.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ConvivenciaPix.SpiCorrelateWorker.Consumers;

public sealed class SystemBSentConsumer : KafkaConsumerBase<string, string>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public SystemBSentConsumer(
        IConfiguration configuration,
        IProducer<string, string> dlqProducer,
        IServiceScopeFactory scopeFactory,
        ISpiMetrics metrics,
        ILogger<SystemBSentConsumer> logger)
        : base(BuildConsumer(configuration), dlqProducer, Topics.SystemBRequests, logger, metrics)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task ProcessMessageAsync(
        ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        var envelope = JsonSerializer.Deserialize<KafkaEnvelope>(result.Message.Value)
            ?? throw new InvalidOperationException("Failed to deserialize KafkaEnvelope from SystemBRequests");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var useCase = scope.ServiceProvider.GetRequiredService<IReceiveSystemBSentUseCase>();
        await useCase.ExecuteAsync(envelope, cancellationToken);
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
