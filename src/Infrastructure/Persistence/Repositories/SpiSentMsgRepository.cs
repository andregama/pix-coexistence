using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace ConvivenciaPix.Infrastructure.Persistence.Repositories;

public sealed class SpiSentMsgRepository : ISpiSentMsgRepository
{
    private readonly CoexistenceDbContext _db;

    public SpiSentMsgRepository(CoexistenceDbContext db) => _db = db;

    public Task<SpiSentMsg?> FindByIdSystemAAsync(string idSystemA, CancellationToken cancellationToken = default) =>
        _db.SpiSentMsgs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.IdSystemA == idSystemA, cancellationToken);

    public Task<SpiSentMsg?> FindByIdSystemBAsync(string idSystemB, CancellationToken cancellationToken = default) =>
        _db.SpiSentMsgs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.IdSystemB == idSystemB, cancellationToken);

    public Task<bool> ExistsAsync(string idSystemA, string idSystemB, CancellationToken cancellationToken = default) =>
        _db.SpiSentMsgs.AsNoTracking()
            .AnyAsync(x => x.IdSystemA == idSystemA || x.IdSystemB == idSystemB, cancellationToken);

    public async Task AddAsync(SpiSentMsg msg, CancellationToken cancellationToken = default)
    {
        _db.SpiSentMsgs.Add(msg);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default) =>
        await _db.SpiSentMsgs
            .Where(x => x.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
}
