namespace ConvivenciaPix.Infrastructure.Messaging.Dlq;

/// <summary>What to do with a dead-lettered message.</summary>
/// <param name="Retry">True to republish at <paramref name="DueAt"/>; false to give up.</param>
/// <param name="DueAt">When the message may be republished (only meaningful when retrying).</param>
/// <param name="Reason">Why the message is given up on (only when not retrying).</param>
public readonly record struct DlqRetryDecision(bool Retry, DateTimeOffset DueAt, string? Reason);

/// <summary>Decides whether and when a dead-lettered message is retried.</summary>
public sealed class DlqRetryPolicy
{
    private readonly DlqRetryOptions _options;
    private readonly IReadOnlySet<string> _nonRetryable;

    public DlqRetryPolicy(DlqRetryOptions options)
    {
        _options = options;
        _nonRetryable = options.GetNonRetryableExceptionTypes();
    }

    /// <param name="retryCount">Retries already made (0 for a message dead-lettered for the first time).</param>
    /// <param name="exceptionType">Value of <c>dlq.exception.type</c>, when present.</param>
    /// <param name="deadLetteredAt">When the message entered the DLQ.</param>
    public DlqRetryDecision Decide(int retryCount, string? exceptionType, DateTimeOffset deadLetteredAt)
    {
        if (exceptionType is not null && _nonRetryable.Contains(exceptionType))
            return new DlqRetryDecision(false, default, $"non-retryable exception {exceptionType}");

        if (retryCount >= _options.MaxRetries)
            return new DlqRetryDecision(false, default, $"retries exhausted ({retryCount}/{_options.MaxRetries})");

        return new DlqRetryDecision(true, deadLetteredAt + ComputeBackoff(retryCount), null);
    }

    private TimeSpan ComputeBackoff(int retryCount)
    {
        var raw = _options.InitialDelaySeconds * Math.Pow(_options.BackoffMultiplier, retryCount);
        var capped = Math.Min(raw, _options.MaxDelaySeconds);
        return TimeSpan.FromSeconds(Math.Max(0, capped));
    }
}
