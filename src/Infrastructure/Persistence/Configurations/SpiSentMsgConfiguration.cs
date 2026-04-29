using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConvivenciaPix.Infrastructure.Persistence.Configurations;

public sealed class SpiSentMsgConfiguration : IEntityTypeConfiguration<SpiSentMsg>
{
    public void Configure(EntityTypeBuilder<SpiSentMsg> builder)
    {
        builder.ToTable("SpiSentMsg");

        // Composite PK creates a clustered index on (IdSystemA, IdSystemB)
        builder.HasKey(x => new { x.IdSystemA, x.IdSystemB });

        builder.Property(x => x.IdSystemA)
            .HasColumnType("VARCHAR(255)")
            .IsRequired();

        builder.Property(x => x.IdSystemB)
            .HasColumnType("VARCHAR(255)")
            .IsRequired();

        // Separate non-clustered unique indexes for the individual-column lookup patterns
        // used by FindByIdSystemAAsync and FindByIdSystemBAsync.
        // NOTE: When reviewing the generated migration DDL, verify these appear as
        // NONCLUSTERED — the clustered index is already taken by the composite PK above.
        builder.HasIndex(x => x.IdSystemA)
            .IsUnique()
            .HasDatabaseName("IX_SpiSentMsg_IdSystemA");

        builder.HasIndex(x => x.IdSystemB)
            .IsUnique()
            .HasDatabaseName("IX_SpiSentMsg_IdSystemB");

        builder.Property(x => x.CorrelationSource)
            .HasColumnType("VARCHAR(20)")
            .IsRequired()
            .HasConversion(
                v => v.Value,
                v => CorrelationSource.From(v));

        builder.Property(x => x.CreatedAt)
            .HasColumnType("DATETIME2")
            .IsRequired();
    }
}
