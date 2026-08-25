using ConvivenciaPix.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConvivenciaPix.Infrastructure.Persistence.Configurations;

public sealed class SpiReceivedMsgConfiguration : IEntityTypeConfiguration<SpiReceivedMsg>
{
    public void Configure(EntityTypeBuilder<SpiReceivedMsg> builder)
    {
        builder.ToTable("SpiReceivedMsg");
        builder.HasKey(x => x.IdempotentId);

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
        builder.Property(x => x.CorrelationSource).HasColumnType("VARCHAR(20)");
        builder.Property(x => x.CreatedAt).HasColumnType("DATETIME2").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnType("DATETIME2");

        builder.HasIndex(x => x.MsgId)
            .HasDatabaseName("IX_SpiReceivedMsg_MsgId")
            .HasFilter("[MsgId] IS NOT NULL");

        builder.HasIndex(x => x.OriginalMsgIdempotentId)
            .HasDatabaseName("IX_SpiReceivedMsg_OriginalMsgIdempotentId")
            .HasFilter("[OriginalMsgIdempotentId] IS NOT NULL");
    }
}
