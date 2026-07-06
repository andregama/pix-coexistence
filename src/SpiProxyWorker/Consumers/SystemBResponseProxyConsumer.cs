using Confluent.Kafka;
using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.PropagateResponse;
using ConvivenciaPix.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ConvivenciaPix.SpiProxyWorker.Consumers;

/// <summary>
/// Consumes the correlate worker's "ready-for-System-B" events (already correlated and
/// transformed) from spi.systemb.responses, signs them, and enqueues them for System B to pull.
/// </summary>
public sealed class SystemBResponseProxyConsumer : KafkaConsumerBase<string, string>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public SystemBResponseProxyConsumer(
        IConfiguration configuration,
        IProducer<string, string> dlqProducer,
        IServiceScopeFactory scopeFactory,
        ISpiMetrics metrics,
        ILogger<SystemBResponseProxyConsumer> logger)
        : base(BuildConsumer(configuration), dlqProducer, Topics.SystemBResponses, logger, metrics)
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

        var envelope = JsonSerializer.Deserialize<KafkaEnvelope>(result.Message.Value)
            ?? throw new InvalidOperationException("Failed to deserialize KafkaEnvelope");
        var readyJson = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.PayloadBase64));
        var ready = JsonSerializer.Deserialize<SystemBInboundReadyDto>(readyJson)
            ?? throw new InvalidOperationException("Failed to deserialize SystemBInboundReadyDto");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var useCase = scope.ServiceProvider.GetRequiredService<IPropagateResponseUseCase>();
        await useCase.ExecuteAsync(ready, cancellationToken);
    }

    private static IConsumer<string, string> BuildConsumer(IConfiguration configuration) =>
        new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = configuration["Kafka:BootstrapServers"]
                ?? throw new InvalidOperationException("Kafka:BootstrapServers is required."),
            GroupId = "spi-proxy-systemb-responses",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();
}
