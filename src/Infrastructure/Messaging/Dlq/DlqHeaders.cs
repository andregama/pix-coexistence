using Confluent.Kafka;
using System.Globalization;
using System.Text;

namespace ConvivenciaPix.Infrastructure.Messaging.Dlq;

/// <summary>
/// Names and accessors of the headers a dead-lettered message carries. Written by
/// <see cref="KafkaConsumerBase{TKey,TValue}"/> and read by <see cref="DlqRetryConsumer"/>.
/// </summary>
public static class DlqHeaders
{
    public const string Prefix = "dlq.";

    public const string SourceTopic = "dlq.source.topic";
    public const string SourcePartition = "dlq.source.partition";
    public const string SourceOffset = "dlq.source.offset";
    public const string ExceptionType = "dlq.exception.type";
    public const string ExceptionMessage = "dlq.exception.message";
    public const string Timestamp = "dlq.timestamp";

    /// <summary>How many times the retry worker has already republished the message.</summary>
    public const string RetryCount = "dlq.retry.count";

    public static string? GetString(Headers? headers, string key) =>
        headers is not null && headers.TryGetLastBytes(key, out var bytes) && bytes is not null
            ? Encoding.UTF8.GetString(bytes)
            : null;

    public static int GetRetryCount(Headers? headers) =>
        int.TryParse(GetString(headers, RetryCount), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count
            : 0;

    public static DateTimeOffset? GetTimestamp(Headers? headers) =>
        DateTimeOffset.TryParse(GetString(headers, Timestamp), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var timestamp)
            ? timestamp
            : null;

    public static byte[] EncodeRetryCount(int count) =>
        Encoding.UTF8.GetBytes(count.ToString(CultureInfo.InvariantCulture));
}
