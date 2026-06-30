using ConvivenciaPix.Domain.Entities;

namespace ConvivenciaPix.Domain.Repositories;

public interface ISpiSentMsgRepository
{
    Task<SpiSentMsg?> FindByIdempotentIdAsync(string idempotentId, CancellationToken cancellationToken = default);
    Task AddAsync(SpiSentMsg msg, CancellationToken cancellationToken = default);
    Task UpdateAsync(SpiSentMsg msg, CancellationToken cancellationToken = default);
    Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default);
}
