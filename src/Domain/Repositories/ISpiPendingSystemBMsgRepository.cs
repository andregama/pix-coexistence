using ConvivenciaPix.Domain.Entities;

namespace ConvivenciaPix.Domain.Repositories;

public interface ISpiPendingSystemBMsgRepository
{
    Task<bool> ExistsByIdSystemBAsync(string idSystemB, CancellationToken cancellationToken = default);
    Task AddAsync(SpiPendingSystemBMsg msg, CancellationToken cancellationToken = default);
    Task<SpiPendingSystemBMsg?> FindHeuristicMatchAsync(
        DateTimeOffset timestamp, decimal amount, string payerId, string payeeId,
        int windowSeconds, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default);
}
