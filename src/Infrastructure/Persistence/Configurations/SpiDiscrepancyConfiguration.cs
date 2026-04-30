using ConvivenciaPix.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConvivenciaPix.Infrastructure.Persistence.Configurations;

public sealed class SpiDiscrepancyConfiguration : IEntityTypeConfiguration<SpiDiscrepancy>
{
    public void Configure(EntityTypeBuilder<SpiDiscrepancy> builder)
    {
        builder.ToTable("SpiDiscrepancies");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.IdSystemA).IsRequired().HasMaxLength(64);
        builder.Property(e => e.IdSystemB).IsRequired().HasMaxLength(64);
        builder.Property(e => e.CorrelationSource).IsRequired().HasMaxLength(32);
        builder.Property(e => e.Field).IsRequired().HasMaxLength(64);
        builder.Property(e => e.SystemAValue).HasMaxLength(512);
        builder.Property(e => e.SystemBValue).HasMaxLength(512);
        builder.Property(e => e.DetectedAt).HasColumnType("datetime2");

        builder.HasIndex(e => new { e.IdSystemA, e.DetectedAt })
            .HasDatabaseName("IX_SpiDiscrepancies_IdSystemA_DetectedAt");
    }
}
