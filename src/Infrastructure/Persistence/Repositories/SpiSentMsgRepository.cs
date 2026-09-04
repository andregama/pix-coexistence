using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ConvivenciaPix.Infrastructure.Persistence.Repositories;

public sealed class SpiSentMsgRepository : ISpiSentMsgRepository
{
    private const int MaxUpsertAttempts = 3;

    private readonly CoexistenceDbContext _db;

    public SpiSentMsgRepository(CoexistenceDbContext db) => _db = db;

    public Task<SpiSentMsg?> FindByIdempotentIdAsync(string idempotentId, CancellationToken cancellationToken = default) =>
        _db.SpiSentMsgs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.IdempotentId == idempotentId, cancellationToken);

    public Task<SpiSentMsg?> FindByMsgIdSystemAAsync(string msgIdSystemA, CancellationToken cancellationToken = default) =>
        _db.SpiSentMsgs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.MsgIdSystemA == msgIdSystemA, cancellationToken);

    public Task<UpsertOutcome<SpiSentMsg>> UpsertSystemAAsync(SpiSentMsg msg, CancellationToken cancellationToken = default) =>
        UpsertAsync(msg,
            update: q => q.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.MsgIdSystemA, msg.MsgIdSystemA)
                .SetProperty(x => x.XmlMsgSystemA, msg.XmlMsgSystemA)
                .SetProperty(x => x.SystemAErrorCode, msg.SystemAErrorCode)
                .SetProperty(x => x.OriginalMsgIdempotentId, x => x.OriginalMsgIdempotentId ?? msg.OriginalMsgIdempotentId)
                .SetProperty(x => x.CorrelationSource, x => x.CorrelationSource ?? msg.CorrelationSource)
                .SetProperty(x => x.UpdatedAt, msg.UpdatedAt), cancellationToken),
            cancellationToken);

    public Task<UpsertOutcome<SpiSentMsg>> UpsertSystemBAsync(SpiSentMsg msg, CancellationToken cancellationToken = default) =>
        UpsertAsync(msg,
            update: q => q.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.MsgIdSystemB, msg.MsgIdSystemB)
                .SetProperty(x => x.XmlMsgSystemB, msg.XmlMsgSystemB)
                .SetProperty(x => x.SystemBErrorCode, msg.SystemBErrorCode)
                .SetProperty(x => x.OriginalMsgIdempotentId, x => x.OriginalMsgIdempotentId ?? msg.OriginalMsgIdempotentId)
                .SetProperty(x => x.CorrelationSource, x => x.CorrelationSource ?? msg.CorrelationSource)
                .SetProperty(x => x.UpdatedAt, msg.UpdatedAt), cancellationToken),
            cancellationToken);

    // Atomic upsert: try a column-scoped UPDATE; if no row matched, INSERT; if the INSERT races
    // another side (duplicate key), retry as an UPDATE. Never clobbers the other side, never throws
    // on the concurrent-insert race.
    private async Task<UpsertOutcome<SpiSentMsg>> UpsertAsync(
        SpiSentMsg msg, Func<IQueryable<SpiSentMsg>, Task<int>> update, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var rows = await update(_db.SpiSentMsgs.Where(x => x.IdempotentId == msg.IdempotentId));
            if (rows > 0)
            {
                var merged = await _db.SpiSentMsgs.AsNoTracking()
                    .FirstAsync(x => x.IdempotentId == msg.IdempotentId, ct);
                return new UpsertOutcome<SpiSentMsg>(merged, Inserted: false);
            }

            try
            {
                _db.SpiSentMsgs.Add(msg);
                await _db.SaveChangesAsync(ct);
                return new UpsertOutcome<SpiSentMsg>(msg, Inserted: true);
            }
            catch (DbUpdateException ex) when (IsUniqueKeyViolation(ex) && attempt < MaxUpsertAttempts)
            {
                _db.ChangeTracker.Clear(); // detach the failed insert; another side won the race — retry as update
            }
        }
    }

    // SQL Server: 2627 = PK/unique constraint violation, 2601 = unique index violation.
    internal static bool IsUniqueKeyViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2627 or 2601 };

    public async Task AddAsync(SpiSentMsg msg, CancellationToken cancellationToken = default)
    {
        _db.SpiSentMsgs.Add(msg);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(SpiSentMsg msg, CancellationToken cancellationToken = default)
    {
        _db.SpiSentMsgs.Update(msg);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default) =>
        _db.SpiSentMsgs
            .Where(x => x.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
}
