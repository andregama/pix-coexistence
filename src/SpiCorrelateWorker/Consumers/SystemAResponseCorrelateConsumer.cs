using Confluent.Kafka;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.CorrelateMessages;
using ConvivenciaPix.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ConvivenciaPix.SpiCorrelateWorker.Consumers;

public sealed class SystemAResponseCorrelateConsumer : KafkaConsumerBase<string, string>
{
    private readonly ICorrelateMessagesUseCase _useCase;

    public SystemAResponseCorrelateConsumer(
        IConfiguration configuration,
        IProducer<string, string> dlqProducer,
        ICorrelateMessagesUseCase useCase,
        ISpiMetrics metrics,
        ILogger<SystemAResponseCorrelateConsumer> logger)
        : base(BuildConsumer(configuration), dlqProducer, Topics.SystemAResponses, logger, metrics)
    {
        _useCase = useCase;
    }

    protected override async Task ProcessMessageAsync(
        ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        await _useCase.ExecuteAsync(result.Message.Value, cancellationToken);
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
