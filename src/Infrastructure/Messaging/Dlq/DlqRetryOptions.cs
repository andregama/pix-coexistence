namespace ConvivenciaPix.Infrastructure.Messaging.Dlq;

/// <summary>Configuration of the DLQ retry worker (section <c>DlqRetry</c>).</summary>
public sealed class DlqRetryOptions
{
    public const string SectionName = "DlqRetry";

    /// <summary>
    /// Comma-separated DLQ topics to retry. A message goes back to the topic named in its
    /// <c>dlq.source.topic</c> header, or to the DLQ's name without the ".dlq" suffix.
    /// </summary>
    public string Topics { get; set; } = string.Join(',',
        Messaging.Topics.SystemBRequestsDlq,
        Messaging.Topics.SystemACdcDlq,
        Messaging.Topics.SystemBResponsesDlq,
        Messaging.Topics.CorrelationEventsDlq,
        Messaging.Topics.ComparisonEventsDlq);

    /// <summary>How many times a message is republished before it is given up on.</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>Wait before the first retry, counted from when the message was dead-lettered.</summary>
    public int InitialDelaySeconds { get; set; } = 30;

    /// <summary>Exponential growth factor applied to the wait on each further retry.</summary>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>Upper bound on the wait before any single retry.</summary>
    public int MaxDelaySeconds { get; set; } = 300;

    /// <summary>Wait before trying again when republishing to the source topic fails.</summary>
    public int RepublishFailureDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Comma-separated exception type names (as in <c>dlq.exception.type</c>) that are never retried:
    /// a malformed payload fails the same way every time.
    /// </summary>
    public string NonRetryableExceptionTypes { get; set; } =
        "System.Text.Json.JsonException,System.Xml.XmlException,System.FormatException";

    public IReadOnlyList<string> GetTopics() => Split(Topics);

    public IReadOnlySet<string> GetNonRetryableExceptionTypes() =>
        Split(NonRetryableExceptionTypes).ToHashSet(StringComparer.Ordinal);

    private static string[] Split(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
