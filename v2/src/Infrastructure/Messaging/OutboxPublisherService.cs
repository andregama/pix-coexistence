using Confluent.Kafka;
using ConvivenciaPix.V2.Domain.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConvivenciaPix.V2.Infrastructure.Messaging;

public sealed class AuditOptions
{
    public bool Enabled { get; set; } = true;
    public string BootstrapServers { get; set; } = string.Empty;
    public int PollSeconds { get; set; } = 2;
    public int BatchSize { get; set; } = 100;
}

/// <summary>
/// Publishes the audit outbox to Kafka. Kafka is not part of the message path: if it is down, events
/// simply accumulate in the outbox and are published when it comes back.
/// </summary>
public sealed class OutboxPublisherService : BackgroundService
{
    private readonly IAuditOutboxRepository _outbox;
    private readonly AuditOptions _options;
    private readonly ILogger<OutboxPublisherService> _logger;
    private IProducer<string, string>? _producer;

    public OutboxPublisherService(
        IAuditOutboxRepository outbox, IOptions<AuditOptions> options, ILogger<OutboxPublisherService> logger)
    {
        _outbox = outbox;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;
        if (string.IsNullOrWhiteSpace(_options.BootstrapServers))
            throw new InvalidOperationException("Audit:BootstrapServers is required when Audit:Enabled is true.");

        _producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = 10_000
        }).Build();

        var idle = TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            var published = 0;
            try
            {
                published = await _outbox.PublishPendingAsync(
                    Math.Max(1, _options.BatchSize),
                    (auditEvent, ct) => _producer.ProduceAsync(
                        auditEvent.Topic,
                        new Message<string, string> { Key = auditEvent.Key, Value = auditEvent.PayloadJson },
                        ct),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogAuditPublishFailed(ex);
            }

            // A full batch means there is probably more waiting: keep going without pausing.
            if (published >= _options.BatchSize) continue;

            try
            {
                await Task.Delay(idle, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public override void Dispose()
    {
        _producer?.Dispose();
        base.Dispose();
    }
}

internal static partial class OutboxPublisherLogMessages
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not publish audit events to Kafka; they stay in the outbox and will be retried")]
    public static partial void LogAuditPublishFailed(this ILogger logger, Exception ex);
}
