using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Domain.Messages;
using ConvivenciaPix.V2.Domain.Repositories;
using ConvivenciaPix.V2.Domain.Routing;

namespace ConvivenciaPix.V2.Infrastructure.Persistence.InMemory;

internal sealed class InMemorySentRouteRepository(InMemoryStore store) : ISentRouteRepository
{
    public Task<SystemName> RegisterAsync(SpiSentRoute route, CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
        {
            if (!store.SentRoutes.TryGetValue(route.MsgId, out var row))
            {
                row = new SentRouteRow(route, store.NextSequence(), store.UtcNow);
                store.SentRoutes.Add(route.MsgId, row);
            }
            return Task.FromResult(row.Route.SystemName);
        }
    }

    public Task MarkForwardedAsync(
        string msgId, string? piResourceId, int httpStatus, CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
        {
            if (store.SentRoutes.TryGetValue(msgId, out var row))
            {
                row.HttpStatus = httpStatus;
                if (piResourceId is not null)
                {
                    row.PiResourceId = piResourceId;
                    store.SentRoutesByResourceId.TryAdd(piResourceId, row);
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task<SystemName?> FindSystemByMsgIdAsync(string msgId, CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
            return Task.FromResult(store.SentRoutes.TryGetValue(msgId, out var row) ? row.Route.SystemName : (SystemName?)null);
    }

    public Task<SystemName?> FindSystemByPiResourceIdAsync(string piResourceId, CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
            return Task.FromResult(
                store.SentRoutesByResourceId.TryGetValue(piResourceId, out var row) ? row.Route.SystemName : (SystemName?)null);
    }

    public Task<SystemName?> ClaimEchoAsync(string echoDataHash, CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
        {
            // Oldest unanswered echo with this content; if every one was already answered (a
            // redelivered reply), the most recent sender.
            var echoes = store.SentRoutes.Values
                .Where(row => row.Route.EchoDataHash == echoDataHash)
                .OrderBy(row => row.Sequence)
                .ToList();

            var claimed = echoes.FirstOrDefault(row => !row.EchoAnswered);
            if (claimed is not null)
                claimed.EchoAnswered = true;

            return Task.FromResult((claimed ?? echoes.LastOrDefault())?.Route.SystemName);
        }
    }

    public Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
        {
            var expired = store.SentRoutes.Values.Where(row => row.CreatedAt < cutoffUtc).Take(batchSize).ToList();
            foreach (var row in expired)
            {
                store.SentRoutes.Remove(row.Route.MsgId);
                if (row.PiResourceId is not null
                    && store.SentRoutesByResourceId.TryGetValue(row.PiResourceId, out var indexed) && indexed == row)
                {
                    store.SentRoutesByResourceId.Remove(row.PiResourceId);
                }
            }
            return Task.FromResult(expired.Count);
        }
    }
}

internal sealed class InMemoryTxAffinityRepository(InMemoryStore store) : ITxAffinityRepository
{
    public Task<SystemName?> FindAsync(TxKey key, CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
            return Task.FromResult(store.Affinities.TryGetValue(key, out var row) ? row.System : (SystemName?)null);
    }

    public Task<SystemName> GetOrAddAsync(
        TxKey key, SystemName system, string origin, CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
            return Task.FromResult(GetOrAdd(store, key, system, origin));
    }

    // Shared with InMemoryInboundMessageRepository, which binds affinities while it stores a message.
    internal static SystemName GetOrAdd(InMemoryStore store, TxKey key, SystemName system, string origin)
    {
        if (!store.Affinities.TryGetValue(key, out var row))
        {
            row = new AffinityRow(system, origin, store.UtcNow);
            store.Affinities.Add(key, row);
        }
        return row.System;
    }

    public Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
        {
            var expired = store.Affinities.Where(pair => pair.Value.CreatedAt < cutoffUtc).Take(batchSize).ToList();
            foreach (var pair in expired)
                store.Affinities.Remove(pair.Key);
            return Task.FromResult(expired.Count);
        }
    }
}

internal sealed class InMemoryInboundMessageRepository(InMemoryStore store) : IInboundMessageRepository
{
    private const string ReceivedOrigin = "Received";

    public Task<bool> ExistsAsync(string piResourceId, CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
            return Task.FromResult(store.Inbound.ContainsKey(piResourceId));
    }

    public Task<bool> PersistAsync(
        SpiInboundMsg message,
        IReadOnlyList<InboundDelivery> deliveries,
        IReadOnlyList<TxBinding> bindings,
        AuditEvent? auditEvent,
        CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
        {
            // Redelivery from Bacen: the message and its deliveries are already stored.
            if (store.Inbound.ContainsKey(message.PiResourceId))
                return Task.FromResult(false);

            var row = new InboundRow(message, store.UtcNow) { Unacked = deliveries.Count };
            store.Inbound.Add(message.PiResourceId, row);

            foreach (var delivery in deliveries)
                store.Pending[delivery.System].Add(new DeliveryRow(row, delivery.Payload));

            foreach (var binding in bindings)
                InMemoryTxAffinityRepository.GetOrAdd(store, binding.Key, binding.System, ReceivedOrigin);

            if (auditEvent is not null)
                store.Outbox.Add(auditEvent);

            return Task.FromResult(true);
        }
    }

    public Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
        {
            // A message still waiting for a system is kept regardless of age.
            var expired = store.Inbound.Values
                .Where(row => row.ReceivedAt < cutoffUtc && row.Unacked == 0)
                .Take(batchSize)
                .ToList();
            foreach (var row in expired)
                store.Inbound.Remove(row.Message.PiResourceId);
            return Task.FromResult(expired.Count);
        }
    }
}

internal sealed class InMemoryDeliveryQueue(InMemoryStore store) : IDeliveryQueue
{
    public Task<IReadOnlyList<DeliveryItem>> ClaimAsync(
        SystemName system, string streamId, int max, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
        {
            var now = store.UtcNow;
            var leaseUntil = now.AddSeconds(Math.Max(1, (int)lease.TotalSeconds));

            var items = new List<DeliveryItem>();
            foreach (var row in store.Pending[system])
            {
                if (items.Count >= max) break;
                if (row.LeaseUntil is not null && row.LeaseUntil >= now) continue;

                row.LeaseStreamId = streamId;
                row.LeaseUntil = leaseUntil;
                items.Add(new DeliveryItem(row.Message.Message.PiResourceId, row.Payload));
            }
            return Task.FromResult<IReadOnlyList<DeliveryItem>>(items);
        }
    }

    public Task<int> AckAsync(SystemName system, string streamId, CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
        {
            // A lease that expired and was claimed by another stream carries that stream's id, so a
            // late ack from the original stream cannot acknowledge it.
            var acked = store.Pending[system].RemoveAll(row =>
            {
                if (row.LeaseStreamId != streamId) return false;
                row.Message.Unacked--;
                return true;
            });
            return Task.FromResult(acked);
        }
    }
}

/// <summary>
/// Audit events waiting for a publisher. Unlike the SQL outbox it keeps nothing once an event has
/// been published: there is no Kafka in this mode and nobody reads published events.
/// </summary>
internal sealed class InMemoryAuditOutboxRepository(InMemoryStore store) : IAuditOutboxRepository
{
    public Task AddAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
            store.Outbox.Add(auditEvent);
        return Task.CompletedTask;
    }

    public async Task<int> PublishPendingAsync(
        int max, Func<AuditEvent, CancellationToken, Task> publish, CancellationToken cancellationToken = default)
    {
        List<AuditEvent> pending;
        lock (store.Gate)
            pending = store.Outbox.Take(max).ToList();

        var published = 0;
        try
        {
            foreach (var auditEvent in pending)
            {
                await publish(auditEvent, cancellationToken);
                published++;
            }
        }
        finally
        {
            // Keep what was published even if a later event failed; the rest is retried.
            lock (store.Gate)
                store.Outbox.RemoveRange(0, published);
        }

        return published;
    }

    public Task<int> DeletePublishedOlderThanAsync(
        DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default) => Task.FromResult(0);
}

/// <summary>Stand-in for <c>TB_DADOS_CONVIVENCIA</c>: starts empty and is filled through the admin API.</summary>
internal sealed class InMemorySystemBDocumentRepository(InMemoryStore store) : ISystemBDocumentRepository
{
    private int _lastId;

    public Task<IReadOnlyCollection<string>> LoadAllDocumentsAsync(CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
        {
            IReadOnlyCollection<string> documents = store.Documents
                .Select(entry => CustomerDocument.Normalize(entry.Documento))
                .Where(document => document.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            return Task.FromResult(documents);
        }
    }

    public Task<IReadOnlyList<SystemBDocumentEntry>> ListAsync(
        string? documento, int skip, int take, CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
        {
            IReadOnlyList<SystemBDocumentEntry> page = store.Documents
                .Where(entry => documento is null || CustomerDocument.Normalize(entry.Documento) == documento)
                .Skip(skip)
                .Take(take)
                .ToList();
            return Task.FromResult(page);
        }
    }

    public Task<SystemBDocumentEntry> AddAsync(
        string conta, string agencia, string documento, CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
        {
            var entry = new SystemBDocumentEntry(++_lastId, conta, agencia, documento, store.UtcNow);
            store.Documents.Add(entry);
            return Task.FromResult(entry);
        }
    }

    public Task<int> RemoveAsync(string documento, CancellationToken cancellationToken = default)
    {
        var normalized = CustomerDocument.Normalize(documento);
        lock (store.Gate)
            return Task.FromResult(store.Documents.RemoveAll(entry => CustomerDocument.Normalize(entry.Documento) == normalized));
    }
}
