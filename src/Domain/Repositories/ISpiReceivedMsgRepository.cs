using ConvivenciaPix.Domain.Entities;

namespace ConvivenciaPix.Domain.Repositories;

public interface ISpiReceivedMsgRepository
{
    Task<SpiReceivedMsg?> FindByIdempotentIdAsync(string idempotentId, CancellationToken cancellationToken = default);
    Task AddAsync(SpiReceivedMsg msg, CancellationToken cancellationToken = default);
    Task UpdateAsync(SpiReceivedMsg msg, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the rows whose <see cref="SpiReceivedMsg.PiResourceId"/> is in <paramref name="piResourceIds"/>
    /// as consumed by System B (sets <c>ConsumedAt</c>), first-wins (already-consumed rows are untouched).
    /// Returns the number of rows updated.
    /// </summary>
    Task<int> MarkConsumedByResourceIdsAsync(IReadOnlyList<string> piResourceIds, DateTime consumedAt, CancellationToken cancellationToken = default);

    Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default);
}
