using ConvivenciaPix.V2.Domain.Messages;
using ConvivenciaPix.V2.Domain.Repositories;
using Microsoft.Data.SqlClient;
using System.Data;

namespace ConvivenciaPix.V2.Infrastructure.Persistence;

public sealed class AuditOutboxRepository(CoexistenceDb db) : IAuditOutboxRepository
{
    internal static SqlCommand InsertCommand(SqlConnection connection, SqlTransaction? transaction, AuditEvent auditEvent)
    {
        var command = connection
            .Command("INSERT INTO dbo.SpiAuditOutbox (Topic, MessageKey, Payload) VALUES (@Topic, @MessageKey, @Payload);", transaction)
            .VarChar("@Topic", auditEvent.Topic, 100)
            .VarChar("@MessageKey", auditEvent.Key);
        command.Parameters.Add("@Payload", SqlDbType.NVarChar, -1).Value = auditEvent.PayloadJson;
        return command;
    }

    public async Task AddAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var command = InsertCommand(connection, null, auditEvent);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> PublishPendingAsync(
        int max, Func<AuditEvent, CancellationToken, Task> publish, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // The row locks are held until commit, so a second publisher skips this batch (READPAST).
        var pending = new List<(long Id, AuditEvent Event)>();
        await using (var select = connection.Command(
            """
            SELECT TOP (@Max) Id, Topic, MessageKey, Payload
            FROM dbo.SpiAuditOutbox WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE PublishedAt IS NULL
            ORDER BY Id;
            """, transaction).Int("@Max", max))
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                pending.Add((reader.GetInt64(0), new AuditEvent(reader.GetString(1), reader.GetString(2), reader.GetString(3))));
        }

        var published = 0;
        try
        {
            foreach (var (id, auditEvent) in pending)
            {
                await publish(auditEvent, cancellationToken);

                await using var mark = connection.Command(
                    "UPDATE dbo.SpiAuditOutbox SET PublishedAt = SYSUTCDATETIME() WHERE Id = @Id;", transaction);
                mark.Parameters.Add("@Id", SqlDbType.BigInt).Value = id;
                await mark.ExecuteNonQueryAsync(cancellationToken);
                published++;
            }
        }
        finally
        {
            // Keep what was published even if a later event failed; the rest is retried.
            await transaction.CommitAsync(CancellationToken.None);
        }

        return published;
    }

    public async Task<int> DeletePublishedOlderThanAsync(
        DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var command = connection
            .Command("DELETE TOP (@Batch) FROM dbo.SpiAuditOutbox WHERE PublishedAt IS NOT NULL AND PublishedAt < @Cutoff;")
            .Int("@Batch", batchSize)
            .DateTime2("@Cutoff", cutoffUtc);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
