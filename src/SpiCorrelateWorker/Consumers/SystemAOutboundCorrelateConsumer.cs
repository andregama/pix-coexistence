using Confluent.Kafka;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.CorrelateMessages;
using ConvivenciaPix.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConvivenciaPix.SpiCorrelateWorker.Consumers;

public sealed class SystemAOutboundCorrelateConsumer : KafkaConsumerBase<string, string>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlySet<string> _allowedTypes;

    public SystemAOutboundCorrelateConsumer(
        IConfiguration configuration,
        IProducer<string, string> dlqProducer,
        IServiceScopeFactory scopeFactory,
        IOptions<CorrelationOptions> options,
        ISpiMetrics metrics,
        ILogger<SystemAOutboundCorrelateConsumer> logger)
        : base(BuildConsumer(configuration), dlqProducer, Topics.SystemAOutbound, logger, metrics)
    {
        _scopeFactory = scopeFactory;
        _allowedTypes = options.Value.GetAllowedSet();
    }

    protected override async Task ProcessMessageAsync(
        ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var useCase = scope.ServiceProvider.GetRequiredService<ICorrelateSystemAOutboundUseCase>();
        await useCase.ExecuteAsync(result.Message.Value, _allowedTypes, cancellationToken);
    }

    private static IConsumer<string, string> BuildConsumer(IConfiguration configuration) =>
        new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = configuration["Kafka:BootstrapServers"]
                ?? throw new InvalidOperationException("Kafka:BootstrapServers is required."),
            GroupId = "spi-correlate-systema-outbound",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();
}
