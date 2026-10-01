using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Domain.Messages;
using ConvivenciaPix.V2.Domain.Repositories;
using ConvivenciaPix.V2.Domain.Routing;
using Microsoft.Data.SqlClient;
using System.Data;

namespace ConvivenciaPix.V2.Infrastructure.Persistence;

public sealed class InboundMessageRepository(CoexistenceDb db) : IInboundMessageRepository
{
    private const string ReceivedOrigin = "Received";

    public async Task<bool> ExistsAsync(string piResourceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var command = connection
            .Command("SELECT 1 FROM dbo.SpiInboundMsg WHERE PiResourceId = @PiResourceId;")
            .VarChar("@PiResourceId", piResourceId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<bool> PersistAsync(
        SpiInboundMsg message,
        IReadOnlyList<InboundDelivery> deliveries,
        IReadOnlyList<TxBinding> bindings,
        AuditEvent? auditEvent,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await using (var insert = connection.Command(
                """
                INSERT INTO dbo.SpiInboundMsg
                    (PiResourceId, MsgId, MsgType, TxKey, OriginalMsgId, RouteSource, Targets, Payload)
                VALUES
                    (@PiResourceId, @MsgId, @MsgType, @TxKey, @OriginalMsgId, @RouteSource, @Targets, @Payload);
                """, transaction)
                .VarChar("@PiResourceId", message.PiResourceId)
                .VarChar("@MsgId", message.MsgId)
                .VarChar("@MsgType", message.MsgType, 20)
                .VarChar("@TxKey", message.TxKey)
                .VarChar("@OriginalMsgId", message.OriginalMsgId)
                .VarChar("@RouteSource", message.RouteSource, 30)
                .VarChar("@Targets", message.Targets, 30))
            {
                insert.Parameters.Add("@Payload", SqlDbType.VarBinary, -1).Value = message.Payload;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (SqlException ex) when (ex.IsDuplicateKey())
        {
            // Redelivery from Bacen: the message and its deliveries are already stored.
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        foreach (var delivery in deliveries)
        {
            await using var insert = connection
                .Command(
                    "INSERT INTO dbo.SpiInboundDelivery (PiResourceId, SystemName, Payload) VALUES (@PiResourceId, @SystemName, @Payload);",
                    transaction)
                .VarChar("@PiResourceId", message.PiResourceId)
                .VarChar("@SystemName", delivery.System.ToString(), 10);
            insert.Parameters.Add("@Payload", SqlDbType.VarBinary, -1).Value = delivery.Payload;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var binding in bindings)
        {
            await using var affinity = TxAffinityRepository.GetOrAddCommand(
                connection, transaction, binding.Key, binding.System, ReceivedOrigin);
            await affinity.ExecuteNonQueryAsync(cancellationToken);
        }

        if (auditEvent is not null)
        {
            await using var outbox = AuditOutboxRepository.InsertCommand(connection, transaction, auditEvent);
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default)
    {
        // Only fully acknowledged messages are purged (deliveries go with them via the cascade); a
        // message still waiting for a system is kept regardless of age.
        const string sql = """
            DELETE TOP (@Batch) m
            FROM dbo.SpiInboundMsg m
            WHERE m.ReceivedAt < @Cutoff
              AND NOT EXISTS (
                  SELECT 1 FROM dbo.SpiInboundDelivery d
                  WHERE d.PiResourceId = m.PiResourceId AND d.AckedAt IS NULL);
            """;

        await using var connection = await db.OpenAsync(cancellationToken);
        await using var command = connection.Command(sql).Int("@Batch", batchSize).DateTime2("@Cutoff", cutoffUtc);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
