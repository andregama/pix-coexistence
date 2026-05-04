using Confluent.Kafka;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.PropagateResponse;
using ConvivenciaPix.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace ConvivenciaPix.SpiProxyWorker.Consumers;

public sealed class SystemAResponseProxyConsumer : KafkaConsumerBase<string, string>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public SystemAResponseProxyConsumer(
        IConfiguration configuration,
        IProducer<string, string> dlqProducer,
        IServiceScopeFactory scopeFactory,
        ISpiMetrics metrics,
        ILogger<SystemAResponseProxyConsumer> logger)
        : base(BuildConsumer(configuration), dlqProducer, Topics.SystemAResponses, logger, metrics)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task ProcessMessageAsync(
        ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        var correlationHeader = result.Message.Headers
            .FirstOrDefault(h => h.Key == "correlation-id");
        if (correlationHeader is not null)
        {
            Activity.Current?.SetBaggage("correlation-id",
                Encoding.UTF8.GetString(correlationHeader.GetValueBytes()));
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var useCase = scope.ServiceProvider.GetRequiredService<IPropagateResponseUseCase>();
        await useCase.ExecuteAsync(result.Message.Value, cancellationToken);
    }

    private static IConsumer<string, string> BuildConsumer(IConfiguration configuration) =>
        new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = configuration["Kafka:BootstrapServers"]
                ?? throw new InvalidOperationException("Kafka:BootstrapServers is required."),
            GroupId = "spi-proxy-systema",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();
}
