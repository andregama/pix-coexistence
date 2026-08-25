using ConvivenciaPix.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConvivenciaPix.Infrastructure.Persistence.Configurations;

public sealed class SpiSentMsgConfiguration : IEntityTypeConfiguration<SpiSentMsg>
{
    public void Configure(EntityTypeBuilder<SpiSentMsg> builder)
    {
        builder.ToTable("SpiSentMsg");
        builder.HasKey(x => x.IdempotentId);

        builder.Property(x => x.IdempotentId)
            .HasColumnType("VARCHAR(255)")
            .IsRequired();

        builder.Property(x => x.MsgType)
            .HasColumnType("VARCHAR(20)")
            .IsRequired();

        builder.Property(x => x.MsgIdSystemA).HasColumnType("VARCHAR(255)");
        builder.Property(x => x.MsgIdSystemB).HasColumnType("VARCHAR(255)");
        builder.Property(x => x.XmlMsgSystemA).HasColumnType("NVARCHAR(MAX)");
        builder.Property(x => x.XmlMsgSystemB).HasColumnType("NVARCHAR(MAX)");
        builder.Property(x => x.OriginalMsgIdempotentId).HasColumnType("VARCHAR(255)");
        builder.Property(x => x.SystemAErrorCode).HasColumnType("VARCHAR(MAX)");
        builder.Property(x => x.SystemBErrorCode).HasColumnType("VARCHAR(MAX)");
        builder.Property(x => x.CorrelationSource).HasColumnType("VARCHAR(20)");
        builder.Property(x => x.CreatedAt).HasColumnType("DATETIME2").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnType("DATETIME2");

        builder.HasIndex(x => x.MsgIdSystemA)
            .HasDatabaseName("IX_SpiSentMsg_MsgIdSystemA")
            .HasFilter("[MsgIdSystemA] IS NOT NULL");

        builder.HasIndex(x => x.MsgIdSystemB)
            .HasDatabaseName("IX_SpiSentMsg_MsgIdSystemB")
            .HasFilter("[MsgIdSystemB] IS NOT NULL");

        builder.HasIndex(x => x.OriginalMsgIdempotentId)
            .HasDatabaseName("IX_SpiSentMsg_OriginalMsgIdempotentId")
            .HasFilter("[OriginalMsgIdempotentId] IS NOT NULL");
    }
}
