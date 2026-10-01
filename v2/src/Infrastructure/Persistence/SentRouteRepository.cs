using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Domain.Messages;
using ConvivenciaPix.V2.Domain.Repositories;

namespace ConvivenciaPix.V2.Infrastructure.Persistence;

public sealed class SentRouteRepository(CoexistenceDb db) : ISentRouteRepository
{
    public async Task<SystemName> RegisterAsync(SpiSentRoute route, CancellationToken cancellationToken = default)
    {
        // Insert-if-absent and read back the owner in one round trip. UPDLOCK+HOLDLOCK serialises
        // two concurrent registrations of the same MsgId so exactly one inserts.
        const string sql = """
            INSERT INTO dbo.SpiSentRoute (MsgId, SystemName, MsgType, TxKey, EchoDataHash)
            SELECT @MsgId, @SystemName, @MsgType, @TxKey, @EchoDataHash
            WHERE NOT EXISTS (SELECT 1 FROM dbo.SpiSentRoute WITH (UPDLOCK, HOLDLOCK) WHERE MsgId = @MsgId);

            SELECT SystemName FROM dbo.SpiSentRoute WHERE MsgId = @MsgId;
            """;

        await using var connection = await db.OpenAsync(cancellationToken);
        await using var command = connection.Command(sql)
            .VarChar("@MsgId", route.MsgId)
            .VarChar("@SystemName", route.SystemName.ToString(), 10)
            .VarChar("@MsgType", route.MsgType, 20)
            .VarChar("@TxKey", route.TxKey)
            .VarChar("@EchoDataHash", route.EchoDataHash);

        var owner = (await command.ExecuteScalarAsync(cancellationToken)).ToSystemName();
        return owner ?? throw new InvalidOperationException($"SpiSentRoute row for MsgId '{route.MsgId}' not found after insert.");
    }

    public async Task MarkForwardedAsync(
        string msgId, string? piResourceId, int httpStatus, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.SpiSentRoute
            SET PiResourceId = COALESCE(@PiResourceId, PiResourceId),
                HttpStatus = @HttpStatus,
                ForwardedAt = SYSUTCDATETIME()
            WHERE MsgId = @MsgId;
            """;

        await using var connection = await db.OpenAsync(cancellationToken);
        await using var command = connection.Command(sql)
            .VarChar("@MsgId", msgId)
            .VarChar("@PiResourceId", piResourceId)
            .Int("@HttpStatus", httpStatus);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SystemName?> FindSystemByMsgIdAsync(string msgId, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var command = connection
            .Command("SELECT SystemName FROM dbo.SpiSentRoute WHERE MsgId = @MsgId;")
            .VarChar("@MsgId", msgId);
        return (await command.ExecuteScalarAsync(cancellationToken)).ToSystemName();
    }

    public async Task<SystemName?> FindSystemByPiResourceIdAsync(
        string piResourceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var command = connection
            .Command("SELECT TOP (1) SystemName FROM dbo.SpiSentRoute WHERE PiResourceId = @PiResourceId;")
            .VarChar("@PiResourceId", piResourceId);
        return (await command.ExecuteScalarAsync(cancellationToken)).ToSystemName();
    }

    public async Task<SystemName?> ClaimEchoAsync(string echoDataHash, CancellationToken cancellationToken = default)
    {
        // Oldest unanswered echo with this content. READPAST lets a concurrent claim take the next
        // one instead of blocking. If every matching echo was already answered (a redelivered
        // reply), fall back to the most recent sender.
        const string sql = """
            DECLARE @claimed TABLE (SystemName VARCHAR(10));

            WITH oldest AS (
                SELECT TOP (1) SystemName, EchoAnsweredAt
                FROM dbo.SpiSentRoute WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE EchoDataHash = @Hash AND EchoAnsweredAt IS NULL
                ORDER BY CreatedAt)
            UPDATE oldest SET EchoAnsweredAt = SYSUTCDATETIME()
            OUTPUT inserted.SystemName INTO @claimed;

            IF EXISTS (SELECT 1 FROM @claimed)
                SELECT SystemName FROM @claimed;
            ELSE
                SELECT TOP (1) SystemName FROM dbo.SpiSentRoute
                WHERE EchoDataHash = @Hash ORDER BY CreatedAt DESC;
            """;

        await using var connection = await db.OpenAsync(cancellationToken);
        await using var command = connection.Command(sql).VarChar("@Hash", echoDataHash);
        return (await command.ExecuteScalarAsync(cancellationToken)).ToSystemName();
    }

    public async Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var command = connection
            .Command("DELETE TOP (@Batch) FROM dbo.SpiSentRoute WHERE CreatedAt < @Cutoff;")
            .Int("@Batch", batchSize)
            .DateTime2("@Cutoff", cutoffUtc);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
