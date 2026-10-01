using ConvivenciaPix.V2.Domain.Routing;

namespace ConvivenciaPix.V2.Domain.Repositories;

public interface ITxAffinityRepository
{
    Task<SystemName?> FindAsync(TxKey key, CancellationToken cancellationToken = default);

    /// <summary>Binds the key to the system unless it is already bound; returns the owner.</summary>
    Task<SystemName> GetOrAddAsync(
        TxKey key, SystemName system, string origin, CancellationToken cancellationToken = default);

    Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default);
}
