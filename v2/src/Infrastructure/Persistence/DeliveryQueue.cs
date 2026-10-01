using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Domain.Messages;
using ConvivenciaPix.V2.Domain.Repositories;

namespace ConvivenciaPix.V2.Infrastructure.Persistence;

public sealed class DeliveryQueue(CoexistenceDb db) : IDeliveryQueue
{
    public async Task<IReadOnlyList<DeliveryItem>> ClaimAsync(
        SystemName system, string streamId, int max, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        // Each delivery carries the content signed for its system; rows written before deliveries
        // had their own payload fall back to the message's.
        // Lease the oldest claimable rows in one statement. READPAST makes parallel streams skip
        // rows another stream is claiming, so no message is handed to two streams at once.
        const string sql = """
            DECLARE @claimed TABLE (PiResourceId VARCHAR(64) COLLATE Latin1_General_BIN2, Seq BIGINT);

            WITH pending AS (
                SELECT TOP (@Max) PiResourceId, Seq, LeaseStreamId, LeaseUntil, DeliveryCount
                FROM dbo.SpiInboundDelivery WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE SystemName = @SystemName
                  AND AckedAt IS NULL
                  AND (LeaseUntil IS NULL OR LeaseUntil < SYSUTCDATETIME())
                ORDER BY Seq)
            UPDATE pending
            SET LeaseStreamId = @StreamId,
                LeaseUntil = DATEADD(SECOND, @LeaseSeconds, SYSUTCDATETIME()),
                DeliveryCount = DeliveryCount + 1
            OUTPUT inserted.PiResourceId, inserted.Seq INTO @claimed;

            SELECT c.PiResourceId, COALESCE(d.Payload, m.Payload)
            FROM @claimed c
            JOIN dbo.SpiInboundDelivery d ON d.Seq = c.Seq
            JOIN dbo.SpiInboundMsg m ON m.PiResourceId = c.PiResourceId
            ORDER BY c.Seq;
            """;

        await using var connection = await db.OpenAsync(cancellationToken);
        await using var command = connection.Command(sql)
            .VarChar("@SystemName", system.ToString(), 10)
            .VarChar("@StreamId", streamId, 32)
            .Int("@Max", max)
            .Int("@LeaseSeconds", Math.Max(1, (int)lease.TotalSeconds));

        var items = new List<DeliveryItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new DeliveryItem(reader.GetString(0), (byte[])reader[1]));
        return items;
    }

    public async Task<int> AckAsync(SystemName system, string streamId, CancellationToken cancellationToken = default)
    {
        // A lease that expired and was claimed by another stream carries that stream's id, so a late
        // ack from the original stream cannot acknowledge it.
        const string sql = """
            UPDATE dbo.SpiInboundDelivery
            SET AckedAt = SYSUTCDATETIME()
            WHERE SystemName = @SystemName AND LeaseStreamId = @StreamId AND AckedAt IS NULL;
            """;

        await using var connection = await db.OpenAsync(cancellationToken);
        await using var command = connection.Command(sql)
            .VarChar("@SystemName", system.ToString(), 10)
            .VarChar("@StreamId", streamId, 32);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
