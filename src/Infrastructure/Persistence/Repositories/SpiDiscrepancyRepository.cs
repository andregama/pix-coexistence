using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;

namespace ConvivenciaPix.Infrastructure.Persistence.Repositories;

public sealed class SpiDiscrepancyRepository : ISpiDiscrepancyRepository
{
    private readonly CoexistenceDbContext _context;

    public SpiDiscrepancyRepository(CoexistenceDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(SpiDiscrepancy discrepancy, CancellationToken cancellationToken)
    {
        await _context.SpiDiscrepancies.AddAsync(discrepancy, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRangeAsync(IEnumerable<SpiDiscrepancy> discrepancies, CancellationToken cancellationToken)
    {
        await _context.SpiDiscrepancies.AddRangeAsync(discrepancies, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }
}
