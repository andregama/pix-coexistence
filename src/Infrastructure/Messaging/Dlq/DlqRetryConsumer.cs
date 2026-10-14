using Confluent.Kafka;
using ConvivenciaPix.Application.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConvivenciaPix.Infrastructure.Messaging.Dlq;

/// <summary>
/// Consumes the DLQ topics and gives each message another chance: after a backoff it is republished
/// to its source topic, where the original consumer processes it again. A message that fails again
/// returns to the DLQ carrying <see cref="DlqHeaders.RetryCount"/>; once <see cref="DlqRetryOptions.MaxRetries"/>
/// is reached (or the failure is of a non-retryable type) it is logged, counted and left behind.
/// Payloads are handled as bytes and never deserialized. This consumer never dead-letters: when the
/// republish fails the offset is not committed and the same message is tried again.
/// </summary>
public sealed class DlqRetryConsumer : BackgroundService
{
    public const string OutcomeRetried = "retried";
    public const string OutcomeExhausted = "exhausted";

    private readonly IConsumer<byte[], byte[]> _consumer;
    private readonly IProducer<byte[], byte[]> _producer;
    private readonly DlqRetryOptions _options;
    private readonly DlqRetryPolicy _policy;
    private readonly ISpiMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DlqRetryConsumer> _logger;

    public DlqRetryConsumer(
        IConsumer<byte[], byte[]> consumer,
        IProducer<byte[], byte[]> producer,
        IOptions<DlqRetryOptions> options,
        ISpiMetrics metrics,
        TimeProvider timeProvider,
        ILogger<DlqRetryConsumer> logger)
    {
        _consumer = consumer;
        _producer = producer;
        _options = options.Value;
        _policy = new DlqRetryPolicy(_options);
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield before blocking so BackgroundService.StartAsync can return immediately
        // (see KafkaConsumerBase).
        await Task.Yield();

        var topics = _options.GetTopics();
        _consumer.Subscribe(topics);
        _logger.LogDlqRetryStarted(string.Join(',', topics));

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<byte[], byte[]>? result = null;
            try
            {
                result = _consumer.Consume(stoppingToken);
                if (result.IsPartitionEOF)
                    continue;

                await HandleAsync(result, stoppingToken);
                Commit(result);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ConsumeException ex)
            {
                _logger.LogDlqRetryConsumeError(ex.Error.Code, ex.Error.Reason);
                if (ex.Error.IsFatal)
                    throw;
            }
            catch (Exception ex)
            {
                if (result is null)
                {
                    _logger.LogDlqRetryUnexpectedError(ex);
                    continue;
                }

                // The message could not be put back on its source topic. Keep it: rewind to it and
                // try again after a pause instead of committing past it.
                _logger.LogDlqRepublishFailed(result.TopicPartitionOffset.ToString(), ex);
                _consumer.Seek(result.TopicPartitionOffset);
                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(Math.Max(0, _options.RepublishFailureDelaySeconds)),
                        _timeProvider, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        _consumer.Close();
    }

    private async Task HandleAsync(ConsumeResult<byte[], byte[]> result, CancellationToken ct)
    {
        var headers = result.Message.Headers;
        var sourceTopic = ResolveSourceTopic(result.Topic, headers);
        var offset = result.TopicPartitionOffset.ToString();

        if (sourceTopic is null)
        {
            _metrics.RecordDlqRetry(result.Topic, OutcomeExhausted);
            _logger.LogDlqRetryExhausted(result.Topic, offset, "source topic cannot be determined");
            return;
        }

        var retryCount = DlqHeaders.GetRetryCount(headers);
        var deadLetteredAt = DlqHeaders.GetTimestamp(headers) ?? MessageTime(result);
        var decision = _policy.Decide(
            retryCount, DlqHeaders.GetString(headers, DlqHeaders.ExceptionType), deadLetteredAt);

        if (!decision.Retry)
        {
            _metrics.RecordDlqRetry(result.Topic, OutcomeExhausted);
            _logger.LogDlqRetryExhausted(result.Topic, offset, decision.Reason!);
            return;
        }

        var wait = decision.DueAt - _timeProvider.GetUtcNow();
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, _timeProvider, ct);

        var message = new Message<byte[], byte[]>
        {
            Key = result.Message.Key,
            Value = result.Message.Value,
            Headers = BuildRetryHeaders(headers, retryCount + 1)
        };
        await _producer.ProduceAsync(sourceTopic, message, ct);

        _metrics.RecordDlqRetry(result.Topic, OutcomeRetried);
        _logger.LogDlqRetried(result.Topic, offset, sourceTopic, retryCount + 1, _options.MaxRetries);
    }

    private DateTimeOffset MessageTime(ConsumeResult<byte[], byte[]> result) =>
        result.Message.Timestamp.Type == TimestampType.NotAvailable
            ? _timeProvider.GetUtcNow()
            : result.Message.Timestamp.UtcDateTime;

    private static string? ResolveSourceTopic(string dlqTopic, Headers? headers)
    {
        var fromHeader = DlqHeaders.GetString(headers, DlqHeaders.SourceTopic);
        if (!string.IsNullOrWhiteSpace(fromHeader))
            return fromHeader;

        const string suffix = ".dlq";
        return dlqTopic.EndsWith(suffix, StringComparison.Ordinal) && dlqTopic.Length > suffix.Length
            ? dlqTopic[..^suffix.Length]
            : null;
    }

    // The republished message is the original one: its own headers plus the retry counter. The
    // dlq.* headers describing the previous failure are dropped — a new failure writes new ones.
    private static Headers BuildRetryHeaders(Headers? original, int retryCount)
    {
        var headers = new Headers();
        foreach (var header in original ?? Enumerable.Empty<IHeader>())
        {
            if (!header.Key.StartsWith(DlqHeaders.Prefix, StringComparison.Ordinal))
                headers.Add(new Header(header.Key, header.GetValueBytes()));
        }

        headers.Add(DlqHeaders.RetryCount, DlqHeaders.EncodeRetryCount(retryCount));
        return headers;
    }

    private void Commit(ConsumeResult<byte[], byte[]> result)
    {
        try
        {
            _consumer.Commit(result);
        }
        catch (KafkaException ex)
        {
            // Non-fatal: the message may be seen again and republished once more (at-least-once).
            _logger.LogDlqRetryCommitError(result.TopicPartitionOffset.ToString(), ex.Error.Reason);
        }
    }
}

internal static partial class DlqRetryConsumerLogMessages
{
    [LoggerMessage(Level = LogLevel.Information, Message = "DLQ retry consumer started on topics {Topics}")]
    public static partial void LogDlqRetryStarted(this ILogger logger, string topics);

    [LoggerMessage(Level = LogLevel.Information, Message = "DLQ message {DlqTopic} at {Offset} republished to {SourceTopic} (retry {Retry}/{MaxRetries})")]
    public static partial void LogDlqRetried(this ILogger logger, string dlqTopic, string offset, string sourceTopic, int retry, int maxRetries);

    [LoggerMessage(Level = LogLevel.Error, Message = "DLQ message {DlqTopic} at {Offset} will not be retried: {Reason}")]
    public static partial void LogDlqRetryExhausted(this ILogger logger, string dlqTopic, string offset, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to republish DLQ message at {Offset}; it will be tried again")]
    public static partial void LogDlqRepublishFailed(this ILogger logger, string offset, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "DLQ retry consume error: [{ErrorCode}] {Reason}")]
    public static partial void LogDlqRetryConsumeError(this ILogger logger, ErrorCode errorCode, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "DLQ retry offset commit failed at {Offset}: {Reason}. Message may be republished again.")]
    public static partial void LogDlqRetryCommitError(this ILogger logger, string offset, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error in the DLQ retry consumer (no message context available)")]
    public static partial void LogDlqRetryUnexpectedError(this ILogger logger, Exception exception);
}
