using ConvivenciaPix.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConvivenciaPix.Infrastructure.Persistence.Configurations;

public sealed class SpiPendingSystemBMsgConfiguration : IEntityTypeConfiguration<SpiPendingSystemBMsg>
{
    public void Configure(EntityTypeBuilder<SpiPendingSystemBMsg> builder)
    {
        builder.ToTable("SpiPendingSystemBMsg");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnType("UNIQUEIDENTIFIER")
            .IsRequired();

        builder.Property(x => x.IdSystemB)
            .HasColumnType("VARCHAR(255)")
            .IsRequired();

        builder.HasIndex(x => x.IdSystemB)
            .IsUnique()
            .HasDatabaseName("IX_SpiPendingSystemBMsg_IdSystemB");

        builder.Property(x => x.MessageId)
            .HasColumnType("VARCHAR(255)")
            .IsRequired();

        builder.Property(x => x.Timestamp)
            .HasColumnType("DATETIMEOFFSET")
            .IsRequired();

        builder.Property(x => x.Amount)
            .HasColumnType("DECIMAL(18,2)")
            .IsRequired();

        builder.Property(x => x.PayerId)
            .HasColumnType("VARCHAR(255)")
            .IsRequired();

        builder.Property(x => x.PayeeId)
            .HasColumnType("VARCHAR(255)")
            .IsRequired();

        builder.Property(x => x.RawXmlBase64)
            .HasColumnType("NVARCHAR(MAX)")
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnType("DATETIME2")
            .IsRequired();

        // Composite index for heuristic match query: WHERE Timestamp IN window AND Amount = x AND PayerId = y AND PayeeId = z
        builder.HasIndex(x => new { x.Timestamp, x.Amount })
            .HasDatabaseName("IX_SpiPendingSystemBMsg_Timestamp_Amount");
    }
}
