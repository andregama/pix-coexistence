using ConvivenciaPix.V2.Domain.Messages;
using ConvivenciaPix.V2.Domain.Routing;

namespace ConvivenciaPix.V2.Domain.Repositories;

public interface IInboundMessageRepository
{
    Task<bool> ExistsAsync(string piResourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the message, its deliveries, the transaction bindings and the audit event in a single
    /// transaction. No deliveries means the message is quarantined: kept, but handed to no system.
    /// Returns false when the message was already stored (redelivery).
    /// </summary>
    Task<bool> PersistAsync(
        SpiInboundMsg message,
        IReadOnlyList<InboundDelivery> deliveries,
        IReadOnlyList<TxBinding> bindings,
        AuditEvent? auditEvent,
        CancellationToken cancellationToken = default);

    Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default);
}
