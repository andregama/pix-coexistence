using Confluent.Kafka;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.Mappers;
using ConvivenciaPix.Application.UseCases.CorrelateMessages;
using ConvivenciaPix.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConvivenciaPix.SpiCorrelateWorker.Consumers;

/// <summary>
/// Consumes the single merged System A CDC topic (both SpiRecepApiBacen and SpiEnvioApiBacen,
/// matching the production Debezium routing) and dispatches each event to the inbound or outbound
/// correlation use case based on the Debezium <c>source.table</c>.
/// </summary>
public sealed class SystemACdcCorrelateConsumer : KafkaConsumerBase<string, string>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlySet<string> _allowedTypes;
    private readonly InboundCorrelationTypes _inboundTypes;
    private readonly ILogger<SystemACdcCorrelateConsumer> _logger;

    public SystemACdcCorrelateConsumer(
        IConfiguration configuration,
        IProducer<string, string> dlqProducer,
        IServiceScopeFactory scopeFactory,
        IOptions<CorrelationOptions> options,
        ISpiMetrics metrics,
        ILogger<SystemACdcCorrelateConsumer> logger)
        : base(BuildConsumer(configuration), dlqProducer, ResolveTopic(configuration), logger, metrics)
    {
        _scopeFactory = scopeFactory;
        _allowedTypes = options.Value.GetAllowedSet();
        _inboundTypes = options.Value.GetInboundCorrelationTypes();
        _logger = logger;
    }

    protected override async Task ProcessMessageAsync(
        ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        var value = result.Message.Value;
        var table = CdcSource.ExtractTable(value);

        await using var scope = _scopeFactory.CreateAsyncScope();
        switch (table)
        {
            case CdcSource.OutboundTable:
                await scope.ServiceProvider.GetRequiredService<ICorrelateSystemAOutboundUseCase>()
                    .ExecuteAsync(value, _allowedTypes, cancellationToken);
                break;

            case CdcSource.InboundTable:
                await scope.ServiceProvider.GetRequiredService<ICorrelateSystemAInboundUseCase>()
                    .ExecuteAsync(value, _inboundTypes, cancellationToken);
                break;

            default:
                _logger.LogWarning(
                    "System A CDC: skipping event with unrecognized source.table='{Table}'", table);
                break;
        }
    }

    private static string ResolveTopic(IConfiguration configuration) =>
        configuration["Kafka:SystemACdcTopic"] ?? Topics.SystemACdc;

    private static IConsumer<string, string> BuildConsumer(IConfiguration configuration) =>
        new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = configuration["Kafka:BootstrapServers"]
                ?? throw new InvalidOperationException("Kafka:BootstrapServers is required."),
            GroupId = "spi-correlate-systema-cdc",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();
}
