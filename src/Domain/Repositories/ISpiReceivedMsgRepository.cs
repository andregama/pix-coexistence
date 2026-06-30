using ConvivenciaPix.Domain.Entities;

namespace ConvivenciaPix.Domain.Repositories;

public interface ISpiReceivedMsgRepository
{
    Task<SpiReceivedMsg?> FindByIdempotentIdAsync(string idempotentId, CancellationToken cancellationToken = default);
    Task AddAsync(SpiReceivedMsg msg, CancellationToken cancellationToken = default);
    Task UpdateAsync(SpiReceivedMsg msg, CancellationToken cancellationToken = default);
    Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default);
}
