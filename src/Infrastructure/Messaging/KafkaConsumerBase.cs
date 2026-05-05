using Confluent.Kafka;
using ConvivenciaPix.Application.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConvivenciaPix.Infrastructure.Messaging;

/// <summary>
/// Base class for all Kafka consumers. Provides DLQ routing on unhandled exceptions.
/// Subclasses must NOT catch exceptions from ProcessMessageAsync — let them propagate
/// here so DLQ routing is applied consistently.
/// </summary>
public abstract class KafkaConsumerBase<TKey, TValue> : BackgroundService
{
    private readonly IConsumer<TKey, TValue> _consumer;
    private readonly IProducer<TKey, TValue> _dlqProducer;
    private readonly string _topic;
    private readonly string _dlqTopic;
    private readonly ILogger _logger;
    private readonly ISpiMetrics _metrics;

    protected KafkaConsumerBase(
        IConsumer<TKey, TValue> consumer,
        IProducer<TKey, TValue> dlqProducer,
        string topic,
        ILogger logger,
        ISpiMetrics metrics)
    {
        _consumer = consumer;
        _dlqProducer = dlqProducer;
        _topic = topic;
        _dlqTopic = Topics.DlqFor(topic);
        _logger = logger;
        _metrics = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield before blocking so BackgroundService.StartAsync can return immediately.
        // Without this, _consumer.Consume() holds the host startup thread indefinitely
        // because it is a synchronous blocking call with no await before it.
        await Task.Yield();

        _consumer.Subscribe(_topic);
        _logger.LogConsumerStarted(_topic);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<TKey, TValue>? result = null;
            try
            {
                result = _consumer.Consume(stoppingToken);
                if (result.IsPartitionEOF)
                    continue;

                await ProcessMessageAsync(result, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ConsumeException ex)
            {
                _logger.LogConsumeError(_topic, ex.Error.Code, ex.Error.Reason);
                // Non-fatal consume errors: log and continue. Fatal errors bubble up.
                if (ex.Error.IsFatal)
                    throw;
            }
            catch (Exception ex)
            {
                if (result is not null)
                    await RouteToDlqAsync(result, ex, stoppingToken);
                else
                    _logger.LogUnroutedError(_topic, ex);
            }
        }

        _consumer.Close();
    }

    protected abstract Task ProcessMessageAsync(
        ConsumeResult<TKey, TValue> result,
        CancellationToken cancellationToken);

    private async Task RouteToDlqAsync(
        ConsumeResult<TKey, TValue> original,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            var dlqMessage = new Message<TKey, TValue>
            {
                Key = original.Message.Key,
                Value = original.Message.Value,
                Headers = BuildDlqHeaders(original, exception)
            };

            await _dlqProducer.ProduceAsync(_dlqTopic, dlqMessage, cancellationToken);
            _metrics.RecordDlqMessage(_dlqTopic);

            _logger.LogDlqRouted(
                _topic,
                _dlqTopic,
                original.TopicPartitionOffset.ToString(),
                exception.GetType().Name,
                exception.Message);
        }
        catch (Exception dlqEx)
        {
            // DLQ write itself failed — log both errors. Do not re-throw; the consumer
            // loop must continue to avoid blocking the partition indefinitely.
            _logger.LogDlqFailed(_dlqTopic, dlqEx.GetType().Name, exception.GetType().Name);
        }
    }

    private static Headers BuildDlqHeaders(ConsumeResult<TKey, TValue> original, Exception exception)
    {
        var headers = new Headers();

        // Copy original headers
        foreach (var header in original.Message.Headers ?? Enumerable.Empty<IHeader>())
            headers.Add(new Header(header.Key, header.GetValueBytes()));

        headers.Add("dlq.source.topic", System.Text.Encoding.UTF8.GetBytes(original.Topic));
        headers.Add("dlq.source.partition", BitConverter.GetBytes(original.Partition.Value));
        headers.Add("dlq.source.offset", BitConverter.GetBytes(original.Offset.Value));
        headers.Add("dlq.exception.type", System.Text.Encoding.UTF8.GetBytes(exception.GetType().FullName ?? "Unknown"));
        headers.Add("dlq.exception.message", System.Text.Encoding.UTF8.GetBytes(exception.Message));
        headers.Add("dlq.timestamp", System.Text.Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O")));

        return headers;
    }
}

internal static partial class KafkaConsumerBaseLogMessages
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Kafka consumer started on topic {Topic}")]
    public static partial void LogConsumerStarted(this ILogger logger, string topic);

    [LoggerMessage(Level = LogLevel.Error, Message = "Kafka consume error on topic {Topic}: [{ErrorCode}] {Reason}")]
    public static partial void LogConsumeError(this ILogger logger, string topic, ErrorCode errorCode, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Routing message from {SourceTopic} to DLQ {DlqTopic} at offset {Offset}. Exception: {ExceptionType} - {ExceptionMessage}")]
    public static partial void LogDlqRouted(this ILogger logger, string sourceTopic, string dlqTopic, string offset, string exceptionType, string exceptionMessage);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Failed to write to DLQ {DlqTopic}. DLQ exception: {DlqExceptionType}. Original exception: {OriginalExceptionType}")]
    public static partial void LogDlqFailed(this ILogger logger, string dlqTopic, string dlqExceptionType, string originalExceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unrouteable error on topic {Topic} (no message context available)")]
    public static partial void LogUnroutedError(this ILogger logger, string topic, Exception exception);
}
