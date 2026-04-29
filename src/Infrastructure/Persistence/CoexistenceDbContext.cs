using ConvivenciaPix.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ConvivenciaPix.Infrastructure.Persistence;

public sealed class CoexistenceDbContext : DbContext
{
    public CoexistenceDbContext(DbContextOptions<CoexistenceDbContext> options) : base(options) { }

    public DbSet<SpiSentMsg> SpiSentMsgs => Set<SpiSentMsg>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CoexistenceDbContext).Assembly);
    }
}
