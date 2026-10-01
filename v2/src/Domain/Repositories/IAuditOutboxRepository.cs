using ConvivenciaPix.V2.Domain.Messages;

namespace ConvivenciaPix.V2.Domain.Repositories;

public interface IAuditOutboxRepository
{
    Task AddAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks up to <paramref name="max"/> unpublished events, hands them to <paramref name="publish"/>
    /// and marks them published when it completes. Returns the number of events published.
    /// </summary>
    Task<int> PublishPendingAsync(
        int max, Func<AuditEvent, CancellationToken, Task> publish, CancellationToken cancellationToken = default);

    Task<int> DeletePublishedOlderThanAsync(DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default);
}
