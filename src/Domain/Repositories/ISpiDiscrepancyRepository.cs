using ConvivenciaPix.Domain.Entities;

namespace ConvivenciaPix.Domain.Repositories;

public interface ISpiDiscrepancyRepository
{
    Task AddAsync(SpiDiscrepancy discrepancy, CancellationToken cancellationToken);
    Task AddRangeAsync(IEnumerable<SpiDiscrepancy> discrepancies, CancellationToken cancellationToken);
}
