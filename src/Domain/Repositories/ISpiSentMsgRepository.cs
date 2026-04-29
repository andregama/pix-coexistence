using ConvivenciaPix.Domain.Entities;

namespace ConvivenciaPix.Domain.Repositories;

public interface ISpiSentMsgRepository
{
    Task<SpiSentMsg?> FindByIdSystemAAsync(string idSystemA, CancellationToken cancellationToken = default);
    Task<SpiSentMsg?> FindByIdSystemBAsync(string idSystemB, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string idSystemA, string idSystemB, CancellationToken cancellationToken = default);
    Task AddAsync(SpiSentMsg msg, CancellationToken cancellationToken = default);
    Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default);
}
