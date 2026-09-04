using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace ConvivenciaPix.Infrastructure.Persistence.Repositories;

public sealed class SpiReceivedMsgRepository : ISpiReceivedMsgRepository
{
    private const int MaxUpsertAttempts = 3;

    private readonly CoexistenceDbContext _db;

    public SpiReceivedMsgRepository(CoexistenceDbContext db) => _db = db;

    public Task<SpiReceivedMsg?> FindByIdempotentIdAsync(string idempotentId, CancellationToken cancellationToken = default) =>
        _db.SpiReceivedMsgs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.IdempotentId == idempotentId, cancellationToken);

    public Task<UpsertOutcome<SpiReceivedMsg>> UpsertSystemAAsync(SpiReceivedMsg msg, CancellationToken cancellationToken = default) =>
        UpsertAsync(msg,
            update: q => q.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.XmlMsgSystemA, msg.XmlMsgSystemA)
                .SetProperty(x => x.SystemAErrorCode, msg.SystemAErrorCode)
                .SetProperty(x => x.MsgId, x => x.MsgId ?? msg.MsgId)
                .SetProperty(x => x.OriginalMsgIdempotentId, x => x.OriginalMsgIdempotentId ?? msg.OriginalMsgIdempotentId)
                .SetProperty(x => x.CorrelationSource, x => x.CorrelationSource ?? msg.CorrelationSource)
                .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), cancellationToken),
            cancellationToken);

    public Task<UpsertOutcome<SpiReceivedMsg>> UpsertSystemBAsync(SpiReceivedMsg msg, CancellationToken cancellationToken = default) =>
        UpsertAsync(msg,
            update: q => q.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.XmlMsgSystemB, msg.XmlMsgSystemB)
                .SetProperty(x => x.SystemBErrorCode, msg.SystemBErrorCode)
                .SetProperty(x => x.PiResourceId, x => x.PiResourceId ?? msg.PiResourceId)
                .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), cancellationToken),
            cancellationToken);

    // Atomic upsert: column-scoped UPDATE, else INSERT, retrying as UPDATE on the concurrent-insert
    // race. Never clobbers the other side's columns (nor ConsumedAt), never throws on the race.
    private async Task<UpsertOutcome<SpiReceivedMsg>> UpsertAsync(
        SpiReceivedMsg msg, Func<IQueryable<SpiReceivedMsg>, Task<int>> update, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var rows = await update(_db.SpiReceivedMsgs.Where(x => x.IdempotentId == msg.IdempotentId));
            if (rows > 0)
            {
                var merged = await _db.SpiReceivedMsgs.AsNoTracking()
                    .FirstAsync(x => x.IdempotentId == msg.IdempotentId, ct);
                return new UpsertOutcome<SpiReceivedMsg>(merged, Inserted: false);
            }

            try
            {
                _db.SpiReceivedMsgs.Add(msg);
                await _db.SaveChangesAsync(ct);
                return new UpsertOutcome<SpiReceivedMsg>(msg, Inserted: true);
            }
            catch (DbUpdateException ex) when (SpiSentMsgRepository.IsUniqueKeyViolation(ex) && attempt < MaxUpsertAttempts)
            {
                _db.ChangeTracker.Clear();
            }
        }
    }

    public async Task AddAsync(SpiReceivedMsg msg, CancellationToken cancellationToken = default)
    {
        _db.SpiReceivedMsgs.Add(msg);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(SpiReceivedMsg msg, CancellationToken cancellationToken = default)
    {
        _db.SpiReceivedMsgs.Update(msg);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<int> MarkConsumedByResourceIdsAsync(
        IReadOnlyList<string> piResourceIds, DateTime consumedAt, CancellationToken cancellationToken = default)
    {
        if (piResourceIds.Count == 0)
            return Task.FromResult(0);

        // Set-based update keyed on IX_SpiReceivedMsg_PiResourceId. First-wins: only rows not
        // already consumed are touched, so re-acked/duplicate stream commits stay idempotent.
        return _db.SpiReceivedMsgs
            .Where(x => x.PiResourceId != null
                        && piResourceIds.Contains(x.PiResourceId)
                        && x.ConsumedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.ConsumedAt, consumedAt)
                    .SetProperty(x => x.UpdatedAt, consumedAt),
                cancellationToken);
    }

    public Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default) =>
        _db.SpiReceivedMsgs
            .Where(x => x.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
}
