using ConvivenciaPix.Application.DTOs;

namespace ConvivenciaPix.Application.Interfaces;

/// <summary>
/// Per-Bacen-SPI outbound stream backing store. Holds signed messages waiting to be
/// pulled by System B via GET /api/v1/out/{ispb}/stream/... and tracks per-stream
/// delivery state so DELETE (or follow-up GET) can ack a batch.
/// </summary>
public interface IOutboundStream
{
    /// <summary>Append a signed message to the queue and wake long-poll waiters.</summary>
    Task EnqueueAsync(string piResourceId, string signedXml, CancellationToken cancellationToken = default);

    /// <summary>
    /// Peek the first <paramref name="max"/> messages without removing them.
    /// If the queue is empty, waits up to <paramref name="longPollTimeout"/> for an enqueue
    /// notification before re-checking. Returns an empty list on timeout.
    /// </summary>
    Task<IReadOnlyList<OutboundMessage>> PeekAsync(int max, TimeSpan longPollTimeout, CancellationToken cancellationToken = default);

    /// <summary>Save the batch of resource ids tracked under <paramref name="streamId"/> with TTL.</summary>
    Task SaveStreamAsync(string streamId, IReadOnlyList<string> piResourceIds, CancellationToken cancellationToken = default);

    /// <summary>Returns the resource ids tracked under <paramref name="streamId"/> and atomically deletes the stream entry. Null if unknown.</summary>
    Task<IReadOnlyList<string>?> GetAndDeleteStreamAsync(string streamId, CancellationToken cancellationToken = default);

    /// <summary>Remove the given messages from the queue (ack/commit a batch).</summary>
    Task CommitAsync(IReadOnlyList<string> piResourceIds, CancellationToken cancellationToken = default);
}
