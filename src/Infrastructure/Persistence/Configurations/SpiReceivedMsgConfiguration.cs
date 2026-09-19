using ConvivenciaPix.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConvivenciaPix.Infrastructure.Persistence.Configurations;

public sealed class SpiReceivedMsgConfiguration : IEntityTypeConfiguration<SpiReceivedMsg>
{
    public void Configure(EntityTypeBuilder<SpiReceivedMsg> builder)
    {
        builder.ToTable("SpiReceivedMsg");
        // Composite key: a single correlation id can carry a primary message (pacs.008 credit) and a
        // later response (pacs.002) that shares its EndToEndId — they must be distinct rows, not one.
        builder.HasKey(x => new { x.IdempotentId, x.MsgType });

        builder.Property(x => x.IdempotentId)
            .HasColumnType("VARCHAR(255)")
            .IsRequired();

        builder.Property(x => x.MsgType)
            .HasColumnType("VARCHAR(20)")
            .IsRequired();

        builder.Property(x => x.MsgId).HasColumnType("VARCHAR(255)");
        builder.Property(x => x.XmlMsgSystemA).HasColumnType("NVARCHAR(MAX)");
        builder.Property(x => x.XmlMsgSystemB).HasColumnType("NVARCHAR(MAX)");
        builder.Property(x => x.OriginalMsgIdempotentId).HasColumnType("VARCHAR(255)");
        builder.Property(x => x.SystemAErrorCode).HasColumnType("VARCHAR(MAX)");
        builder.Property(x => x.SystemBErrorCode).HasColumnType("VARCHAR(MAX)");
        builder.Property(x => x.TransferAmount).HasColumnType("DECIMAL(18,2)");
        builder.Property(x => x.WithdrawalAmount).HasColumnType("DECIMAL(18,2)");
        builder.Property(x => x.TxStatus).HasColumnType("VARCHAR(10)");
        builder.Property(x => x.CorrelationSource).HasColumnType("VARCHAR(20)");
        builder.Property(x => x.PiResourceId).HasColumnType("VARCHAR(255)");
        builder.Property(x => x.ConsumedAt).HasColumnType("DATETIME2");
        builder.Property(x => x.CreatedAt).HasColumnType("DATETIME2").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnType("DATETIME2");

        builder.HasIndex(x => x.MsgId)
            .HasDatabaseName("IX_SpiReceivedMsg_MsgId")
            .HasFilter("[MsgId] IS NOT NULL");

        builder.HasIndex(x => x.PiResourceId)
            .HasDatabaseName("IX_SpiReceivedMsg_PiResourceId")
            .HasFilter("[PiResourceId] IS NOT NULL");

        builder.HasIndex(x => x.OriginalMsgIdempotentId)
            .HasDatabaseName("IX_SpiReceivedMsg_OriginalMsgIdempotentId")
            .HasFilter("[OriginalMsgIdempotentId] IS NOT NULL");
    }
}
