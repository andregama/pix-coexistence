using Confluent.Kafka;
using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.CorrelateMessages;
using ConvivenciaPix.Application.UseCases.GeneratePibr002;
using ConvivenciaPix.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace ConvivenciaPix.SpiCorrelateWorker.Consumers;

public sealed class SystemBOutboundCorrelateConsumer : KafkaConsumerBase<string, string>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlySet<string> _allowedTypes;
    private readonly ISpiXmlParser _xmlParser;
    private readonly ILogger<SystemBOutboundCorrelateConsumer> _logger;

    public SystemBOutboundCorrelateConsumer(
        IConfiguration configuration,
        IProducer<string, string> dlqProducer,
        IServiceScopeFactory scopeFactory,
        IOptions<CorrelationOptions> options,
        ISpiXmlParser xmlParser,
        ISpiMetrics metrics,
        ILogger<SystemBOutboundCorrelateConsumer> logger)
        : base(BuildConsumer(configuration), dlqProducer, Topics.SystemBRequests, logger, metrics)
    {
        _scopeFactory = scopeFactory;
        _allowedTypes = options.Value.GetAllowedSet();
        _xmlParser = xmlParser;
        _logger = logger;
    }

    protected override async Task ProcessMessageAsync(
        ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        var envelope = JsonSerializer.Deserialize<KafkaEnvelope>(result.Message.Value)
            ?? throw new InvalidOperationException("Failed to deserialize KafkaEnvelope");

        await using var scope = _scopeFactory.CreateAsyncScope();

        // pibr.001 (SPI Echo) is self-triggered by System B with no System A counterpart, so it is
        // answered by generating a pibr.002 rather than correlated. Dispatch by message type,
        // mirroring SystemACdcCorrelateConsumer's dispatch-by-table. Any other type (or a parse
        // failure) falls through to the normal outbound correlation, which applies its own gate.
        if (ResolveMsgType(envelope) == "pibr.001")
        {
            await scope.ServiceProvider.GetRequiredService<IGeneratePibr002UseCase>()
                .ExecuteAsync(envelope, cancellationToken);
            return;
        }

        await scope.ServiceProvider.GetRequiredService<ICorrelateSystemBOutboundUseCase>()
            .ExecuteAsync(envelope, _allowedTypes, cancellationToken);
    }

    private string? ResolveMsgType(KafkaEnvelope envelope)
    {
        try
        {
            var rawXml = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.PayloadBase64));
            return _xmlParser.ExtractMessageType(rawXml);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            // Undeterminable type: let the correlation use case handle it (skip or DLQ).
            _logger.LogDebug("SystemB outbound: could not resolve MsgType for dispatch: {Error}", ex.Message);
            return null;
        }
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
