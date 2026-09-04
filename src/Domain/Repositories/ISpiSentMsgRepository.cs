using ConvivenciaPix.Domain.Entities;

namespace ConvivenciaPix.Domain.Repositories;

public interface ISpiSentMsgRepository
{
    Task<SpiSentMsg?> FindByIdempotentIdAsync(string idempotentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the row whose System A message id equals <paramref name="msgIdSystemA"/> (served by
    /// IX_SpiSentMsg_MsgIdSystemA). Used to correlate message-level responses such as admi.002
    /// rejections, which reference the original message by its MsgId rather than a transaction key.
    /// </summary>
    Task<SpiSentMsg?> FindByMsgIdSystemAAsync(string msgIdSystemA, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically upserts System A's columns for <paramref name="msg"/>'s IdempotentId (create if
    /// missing, else update only the A columns). Concurrency-safe — never throws on the insert race
    /// and never clobbers System B's columns. Shared/first-wins fields (CorrelationSource,
    /// OriginalMsgIdempotentId) are only set when currently null.
    /// </summary>
    Task<UpsertOutcome<SpiSentMsg>> UpsertSystemAAsync(SpiSentMsg msg, CancellationToken cancellationToken = default);

    /// <summary>Atomic upsert of System B's columns. See <see cref="UpsertSystemAAsync"/>.</summary>
    Task<UpsertOutcome<SpiSentMsg>> UpsertSystemBAsync(SpiSentMsg msg, CancellationToken cancellationToken = default);

    Task AddAsync(SpiSentMsg msg, CancellationToken cancellationToken = default);
    Task UpdateAsync(SpiSentMsg msg, CancellationToken cancellationToken = default);
    Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default);
}
