using ConvivenciaPix.V2.Domain.Messages;

namespace ConvivenciaPix.V2.Domain.Repositories;

public interface ISentRouteRepository
{
    /// <summary>
    /// Inserts the route unless a row with the same MsgId already exists. Returns the system that
    /// owns the MsgId afterwards (the first writer wins).
    /// </summary>
    Task<SystemName> RegisterAsync(SpiSentRoute route, CancellationToken cancellationToken = default);

    Task MarkForwardedAsync(
        string msgId, string? piResourceId, int httpStatus, CancellationToken cancellationToken = default);

    Task<SystemName?> FindSystemByMsgIdAsync(string msgId, CancellationToken cancellationToken = default);

    Task<SystemName?> FindSystemByPiResourceIdAsync(string piResourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims the oldest unanswered pibr.001 whose content hash matches and returns its sender, so
    /// that identical echoes sent by both systems each receive exactly one reply.
    /// </summary>
    Task<SystemName?> ClaimEchoAsync(string echoDataHash, CancellationToken cancellationToken = default);

    Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default);
}
