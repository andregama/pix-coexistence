using Confluent.Kafka;
using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Events;
using ConvivenciaPix.Domain.Repositories;
using ConvivenciaPix.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace ConvivenciaPix.SpiComparisonEngine.Consumers;

/// <summary>
/// Compares business fields (Amount, PayerId, PayeeId) from System A and System B SPI messages.
/// Publishes a SpiDiscrepancyDetected event to spi.discrepancies when any field mismatches.
/// Ignores transaction-specific identifiers (EndToEndId, MessageId) since they are system-generated.
/// </summary>
public sealed class SpiComparisonConsumer : KafkaConsumerBase<string, string>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISpiXmlParser _xmlParser;
    private readonly IKafkaPublisher _publisher;
    private readonly ISpiMetrics _metrics;
    private readonly ILogger<SpiComparisonConsumer> _logger;

    public SpiComparisonConsumer(
        IConfiguration configuration,
        IProducer<string, string> dlqProducer,
        IServiceScopeFactory scopeFactory,
        ISpiXmlParser xmlParser,
        IKafkaPublisher publisher,
        ISpiMetrics metrics,
        ILogger<SpiComparisonConsumer> logger)
        : base(BuildConsumer(configuration), dlqProducer, Topics.ComparisonEvents, logger, metrics)
    {
        _scopeFactory = scopeFactory;
        _xmlParser = xmlParser;
        _publisher = publisher;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ProcessMessageAsync(
        ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        var envelope = JsonSerializer.Deserialize<KafkaEnvelope>(result.Message.Value)
            ?? throw new InvalidOperationException("Failed to deserialize KafkaEnvelope from ComparisonEvents");

        var comparisonEvent = JsonSerializer.Deserialize<SpiComparisonEventDto>(
            Encoding.UTF8.GetString(Convert.FromBase64String(envelope.PayloadBase64)))
            ?? throw new InvalidOperationException("Failed to deserialize SpiComparisonEventDto");

        var discrepancies = CompareFields(comparisonEvent);

        _logger.LogComparisonResult(
            comparisonEvent.IdempotentId, comparisonEvent.MsgType, discrepancies.Count);

        if (discrepancies.Count > 0)
        {
            await PersistDiscrepanciesAsync(comparisonEvent, discrepancies, cancellationToken);
            await PublishDiscrepancyAsync(comparisonEvent, discrepancies, cancellationToken);
        }
    }

    private List<DiscrepancyRecord> CompareFields(SpiComparisonEventDto ev)
    {
        var discrepancies = new List<DiscrepancyRecord>();

        CompareField(discrepancies, "Amount",
            SafeExtract(() => _xmlParser.ExtractAmount(ev.SystemAXml).ToString("F2")),
            SafeExtract(() => _xmlParser.ExtractAmount(ev.SystemBXml).ToString("F2")));

        CompareField(discrepancies, "PayerId",
            SafeExtract(() => _xmlParser.ExtractPayerId(ev.SystemAXml)),
            SafeExtract(() => _xmlParser.ExtractPayerId(ev.SystemBXml)));

        CompareField(discrepancies, "PayeeId",
            SafeExtract(() => _xmlParser.ExtractPayeeId(ev.SystemAXml)),
            SafeExtract(() => _xmlParser.ExtractPayeeId(ev.SystemBXml)));

        return discrepancies;
    }

    private static void CompareField(
        List<DiscrepancyRecord> discrepancies, string fieldName,
        string? valueA, string? valueB)
    {
        if (valueA != valueB)
            discrepancies.Add(new DiscrepancyRecord(fieldName, valueA, valueB));
    }

    private static string? SafeExtract(Func<string> extract)
    {
        try { return extract(); }
        catch { return null; }
    }

    private async Task PersistDiscrepanciesAsync(
        SpiComparisonEventDto ev,
        List<DiscrepancyRecord> discrepancies,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISpiDiscrepancyRepository>();

        var entities = discrepancies.Select(d => SpiDiscrepancy.Create(
            ev.IdempotentId, ev.MsgType, d.FieldName, d.ValueA, d.ValueB)).ToList();

        await repo.AddRangeAsync(entities, cancellationToken);

        foreach (var d in discrepancies)
            _metrics.RecordDiscrepancy(d.FieldName);
    }

    private async Task PublishDiscrepancyAsync(
        SpiComparisonEventDto ev,
        List<DiscrepancyRecord> discrepancies,
        CancellationToken cancellationToken)
    {
        var discrepancyEvent = new SpiDiscrepancyDetected(
            IdempotentId: ev.IdempotentId,
            MsgType: ev.MsgType,
            Discrepancies: discrepancies,
            DetectedAt: DateTimeOffset.UtcNow);

        var envelope = new KafkaEnvelope(
            MessageId: ev.IdempotentId,
            PayloadBase64: Convert.ToBase64String(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(discrepancyEvent))),
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: ev.IdempotentId);

        await _publisher.PublishAsync(Topics.Discrepancies, envelope, cancellationToken);

        _logger.LogDiscrepancyPublished(ev.IdempotentId, discrepancies.Count);
    }

    private static IConsumer<string, string> BuildConsumer(IConfiguration configuration) =>
        new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = configuration["Kafka:BootstrapServers"]
                ?? throw new InvalidOperationException("Kafka:BootstrapServers is required."),
            GroupId = "spi-comparison",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();
}

internal static partial class SpiComparisonConsumerLogMessages
{
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Comparison complete. IdempotentId={IdempotentId} MsgType={MsgType} Discrepancies={Count}")]
    public static partial void LogComparisonResult(this ILogger logger, string idempotentId, string msgType, int count);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Discrepancy detected and published. IdempotentId={IdempotentId} Fields={Count}")]
    public static partial void LogDiscrepancyPublished(this ILogger logger, string idempotentId, int count);
}
