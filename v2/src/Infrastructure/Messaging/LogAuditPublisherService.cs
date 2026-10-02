using ConvivenciaPix.V2.Domain.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConvivenciaPix.V2.Infrastructure.Messaging;

/// <summary>
/// Takes the place of <see cref="OutboxPublisherService"/> when storage is in memory: there is no
/// Kafka in that mode, so audit events are written to the log instead.
/// </summary>
public sealed class LogAuditPublisherService(
    IAuditOutboxRepository outbox, IOptions<AuditOptions> options, ILogger<LogAuditPublisherService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return;

        var batchSize = Math.Max(1, options.Value.BatchSize);
        var idle = TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            var published = await outbox.PublishPendingAsync(
                batchSize,
                (auditEvent, _) =>
                {
                    logger.LogAuditEvent(auditEvent.Topic, auditEvent.Key, auditEvent.PayloadJson);
                    return Task.CompletedTask;
                },
                stoppingToken);

            if (published >= batchSize) continue;

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
}

internal static partial class LogAuditPublisherLogMessages
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Audit {Topic} {Key}: {Payload}")]
    public static partial void LogAuditEvent(this ILogger logger, string topic, string key, string payload);
}
