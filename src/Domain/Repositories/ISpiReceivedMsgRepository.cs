using ConvivenciaPix.Domain.Entities;

namespace ConvivenciaPix.Domain.Repositories;

public interface ISpiReceivedMsgRepository
{
    Task<SpiReceivedMsg?> FindByIdempotentIdAsync(string idempotentId, CancellationToken cancellationToken = default);
    Task AddAsync(SpiReceivedMsg msg, CancellationToken cancellationToken = default);
    Task UpdateAsync(SpiReceivedMsg msg, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically upserts System A's columns for <paramref name="msg"/>'s IdempotentId (create if
    /// missing, else update only the A columns). Concurrency-safe. Shared/first-wins fields
    /// (MsgId, CorrelationSource, OriginalMsgIdempotentId) are only set when currently null.
    /// </summary>
    Task<UpsertOutcome<SpiReceivedMsg>> UpsertSystemAAsync(SpiReceivedMsg msg, CancellationToken cancellationToken = default);

    /// <summary>Atomic upsert of System B's columns (XmlMsgSystemB, error, PiResourceId). Never touches ConsumedAt.</summary>
    Task<UpsertOutcome<SpiReceivedMsg>> UpsertSystemBAsync(SpiReceivedMsg msg, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the rows whose <see cref="SpiReceivedMsg.PiResourceId"/> is in <paramref name="piResourceIds"/>
    /// as consumed by System B (sets <c>ConsumedAt</c>), first-wins (already-consumed rows are untouched).
    /// Returns the number of rows updated.
    /// </summary>
    Task<int> MarkConsumedByResourceIdsAsync(IReadOnlyList<string> piResourceIds, DateTime consumedAt, CancellationToken cancellationToken = default);

    Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default);
}
