using ConvivenciaPix.V2.Domain;

namespace ConvivenciaPix.V2.Application.Interfaces;

/// <summary>
/// Wake-up signal for long-polling pull streams. Purely an optimisation: the delivery queue lives in
/// SQL, so a lost notification only delays delivery until the next poll.
/// </summary>
public interface IDeliveryNotifier
{
    Task NotifyAsync(SystemName system, CancellationToken cancellationToken = default);

    /// <summary>
    /// A task that completes on the first notification for the system after this call. Take it
    /// before checking the queue, so a message enqueued in between is not missed.
    /// </summary>
    Task NextNotification(SystemName system);
}
