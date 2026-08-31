using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace ConvivenciaPix.Infrastructure.Persistence.Repositories;

public sealed class SpiReceivedMsgRepository : ISpiReceivedMsgRepository
{
    private readonly CoexistenceDbContext _db;

    public SpiReceivedMsgRepository(CoexistenceDbContext db) => _db = db;

    public Task<SpiReceivedMsg?> FindByIdempotentIdAsync(string idempotentId, CancellationToken cancellationToken = default) =>
        _db.SpiReceivedMsgs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.IdempotentId == idempotentId, cancellationToken);

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
