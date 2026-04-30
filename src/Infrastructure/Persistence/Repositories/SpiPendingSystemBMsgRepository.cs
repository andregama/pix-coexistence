using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace ConvivenciaPix.Infrastructure.Persistence.Repositories;

public sealed class SpiPendingSystemBMsgRepository : ISpiPendingSystemBMsgRepository
{
    private readonly CoexistenceDbContext _db;

    public SpiPendingSystemBMsgRepository(CoexistenceDbContext db) => _db = db;

    public Task<bool> ExistsByIdSystemBAsync(string idSystemB, CancellationToken cancellationToken = default) =>
        _db.SpiPendingSystemBMsgs.AsNoTracking()
            .AnyAsync(x => x.IdSystemB == idSystemB, cancellationToken);

    public async Task AddAsync(SpiPendingSystemBMsg msg, CancellationToken cancellationToken = default)
    {
        _db.SpiPendingSystemBMsgs.Add(msg);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<SpiPendingSystemBMsg?> FindHeuristicMatchAsync(
        DateTimeOffset timestamp, decimal amount, string payerId, string payeeId,
        int windowSeconds, CancellationToken cancellationToken = default)
    {
        var lower = timestamp.AddSeconds(-windowSeconds);
        var upper = timestamp.AddSeconds(windowSeconds);

        return _db.SpiPendingSystemBMsgs.AsNoTracking()
            .Where(x =>
                x.Timestamp >= lower && x.Timestamp <= upper &&
                x.Amount == amount &&
                x.PayerId == payerId &&
                x.PayeeId == payeeId)
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _db.SpiPendingSystemBMsgs
            .Where(x => x.Id == id)
            .ExecuteDeleteAsync(cancellationToken);

    public async Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default) =>
        await _db.SpiPendingSystemBMsgs
            .Where(x => x.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
}
