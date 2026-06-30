using ConvivenciaPix.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ConvivenciaPix.Infrastructure.Persistence;

public sealed class CoexistenceDbContext : DbContext
{
    public CoexistenceDbContext(DbContextOptions<CoexistenceDbContext> options) : base(options) { }

    public DbSet<SpiSentMsg> SpiSentMsgs => Set<SpiSentMsg>();
    public DbSet<SpiReceivedMsg> SpiReceivedMsgs => Set<SpiReceivedMsg>();
    public DbSet<SpiDiscrepancy> SpiDiscrepancies => Set<SpiDiscrepancy>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CoexistenceDbContext).Assembly);
    }
}
