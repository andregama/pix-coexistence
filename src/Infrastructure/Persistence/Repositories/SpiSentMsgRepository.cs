using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace ConvivenciaPix.Infrastructure.Persistence.Repositories;

public sealed class SpiSentMsgRepository : ISpiSentMsgRepository
{
    private readonly CoexistenceDbContext _db;

    public SpiSentMsgRepository(CoexistenceDbContext db) => _db = db;

    public Task<SpiSentMsg?> FindByIdempotentIdAsync(string idempotentId, CancellationToken cancellationToken = default) =>
        _db.SpiSentMsgs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.IdempotentId == idempotentId, cancellationToken);

    public Task<SpiSentMsg?> FindByMsgIdSystemAAsync(string msgIdSystemA, CancellationToken cancellationToken = default) =>
        _db.SpiSentMsgs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.MsgIdSystemA == msgIdSystemA, cancellationToken);

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
