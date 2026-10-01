using ConvivenciaPix.V2.Domain.Messages;

namespace ConvivenciaPix.V2.Domain.Repositories;

/// <summary>Per-system queue of inbound messages waiting to be pulled by that system.</summary>
public interface IDeliveryQueue
{
    /// <summary>
    /// Atomically leases up to <paramref name="max"/> pending messages to the stream, oldest first.
    /// Messages whose lease expired without an ack become claimable again.
    /// </summary>
    Task<IReadOnlyList<DeliveryItem>> ClaimAsync(
        SystemName system, string streamId, int max, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>Acknowledges everything leased to the stream; returns the number of messages acked.</summary>
    Task<int> AckAsync(SystemName system, string streamId, CancellationToken cancellationToken = default);
}
