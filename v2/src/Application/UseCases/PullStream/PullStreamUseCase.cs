using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Domain.Messages;
using ConvivenciaPix.V2.Domain.Repositories;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace ConvivenciaPix.V2.Application.UseCases.PullStream;

public sealed class DeliveryStreamOptions
{
    public int LongPollSeconds { get; set; } = 5;
    public int MaxMessagesPerMultipart { get; set; } = 10;

    /// <summary>How long a pulled batch stays reserved for its stream before it is delivered again.</summary>
    public int LeaseSeconds { get; set; } = 60;

    /// <summary>Upper bound between queue checks while long-polling (covers lost wake-ups).</summary>
    public int PollIntervalMs { get; set; } = 500;
}

public sealed record PullStreamResult(string NextStreamId, IReadOnlyList<DeliveryItem> Messages);

public interface IPullStreamUseCase
{
    /// <summary>
    /// Acks the batch delivered under <paramref name="previousStreamId"/> (if any) and long-polls for
    /// the next batch addressed to <paramref name="system"/>.
    /// </summary>
    Task<PullStreamResult> ExecuteAsync(
        SystemName system, string? previousStreamId, bool multipart, CancellationToken cancellationToken = default);
}

public sealed class PullStreamUseCase : IPullStreamUseCase
{
    private readonly IDeliveryQueue _queue;
    private readonly IDeliveryNotifier _notifier;
    private readonly DeliveryStreamOptions _options;
    private readonly TimeProvider _time;

    public PullStreamUseCase(
        IDeliveryQueue queue, IDeliveryNotifier notifier, IOptions<DeliveryStreamOptions> options, TimeProvider time)
    {
        _queue = queue;
        _notifier = notifier;
        _options = options.Value;
        _time = time;
    }

    public async Task<PullStreamResult> ExecuteAsync(
        SystemName system, string? previousStreamId, bool multipart, CancellationToken cancellationToken = default)
    {
        // Following PI-Pull-Next confirms receipt of the previous batch.
        if (!string.IsNullOrEmpty(previousStreamId))
            await _queue.AckAsync(system, previousStreamId, cancellationToken);

        var streamId = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var max = multipart ? Math.Max(1, _options.MaxMessagesPerMultipart) : 1;
        var lease = TimeSpan.FromSeconds(_options.LeaseSeconds);
        var pollInterval = TimeSpan.FromMilliseconds(Math.Max(50, _options.PollIntervalMs));
        var deadline = _time.GetUtcNow() + TimeSpan.FromSeconds(_options.LongPollSeconds);

        while (true)
        {
            var signal = _notifier.NextNotification(system);

            var batch = await _queue.ClaimAsync(system, streamId, max, lease, cancellationToken);
            if (batch.Count > 0)
                return new PullStreamResult(streamId, batch);

            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                return new PullStreamResult(streamId, batch);

            var wait = remaining < pollInterval ? remaining : pollInterval;
            await Task.WhenAny(signal, Task.Delay(wait, _time, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
