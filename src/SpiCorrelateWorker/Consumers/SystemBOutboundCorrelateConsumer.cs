using Confluent.Kafka;
using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.CorrelateMessages;
using ConvivenciaPix.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace ConvivenciaPix.SpiCorrelateWorker.Consumers;

public sealed class SystemBOutboundCorrelateConsumer : KafkaConsumerBase<string, string>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlySet<string> _allowedTypes;

    public SystemBOutboundCorrelateConsumer(
        IConfiguration configuration,
        IProducer<string, string> dlqProducer,
        IServiceScopeFactory scopeFactory,
        IOptions<CorrelationOptions> options,
        ISpiMetrics metrics,
        ILogger<SystemBOutboundCorrelateConsumer> logger)
        : base(BuildConsumer(configuration), dlqProducer, Topics.SystemBRequests, logger, metrics)
    {
        _scopeFactory = scopeFactory;
        _allowedTypes = options.Value.GetAllowedSet();
    }

    protected override async Task ProcessMessageAsync(
        ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        var envelope = JsonSerializer.Deserialize<KafkaEnvelope>(result.Message.Value)
            ?? throw new InvalidOperationException("Failed to deserialize KafkaEnvelope");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var useCase = scope.ServiceProvider.GetRequiredService<ICorrelateSystemBOutboundUseCase>();
        await useCase.ExecuteAsync(envelope, _allowedTypes, cancellationToken);
    }

    private static IConsumer<string, string> BuildConsumer(IConfiguration configuration) =>
        new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = configuration["Kafka:BootstrapServers"]
                ?? throw new InvalidOperationException("Kafka:BootstrapServers is required."),
            GroupId = "spi-correlate-systemb-outbound",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();
}
