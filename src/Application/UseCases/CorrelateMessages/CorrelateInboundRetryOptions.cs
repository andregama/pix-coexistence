namespace ConvivenciaPix.Application.UseCases.CorrelateMessages;

/// <summary>
/// Bounds the in-process retry that <see cref="CorrelateSystemAInboundUseCase"/> applies while the
/// correlated <c>SpiSentMsg</c> row is still missing or incomplete — the race where a Bacen inbound
/// response is processed before System B's outbound side has been persisted. Once attempts are
/// exhausted the use case throws and the event is dead-lettered.
/// </summary>
public sealed class CorrelateInboundRetryOptions
{
    /// <summary>Total attempts including the first (must be >= 1).</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Delay before the second attempt; grows by <see cref="BackoffMultiplier"/> each attempt.</summary>
    public int InitialDelayMs { get; set; } = 200;

    /// <summary>Exponential growth factor applied to the delay between attempts.</summary>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>Upper bound on any single inter-attempt delay.</summary>
    public int MaxDelayMs { get; set; } = 5000;
}
