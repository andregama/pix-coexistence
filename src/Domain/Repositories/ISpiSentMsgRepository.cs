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

    Task AddAsync(SpiSentMsg msg, CancellationToken cancellationToken = default);
    Task UpdateAsync(SpiSentMsg msg, CancellationToken cancellationToken = default);
    Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default);
}
