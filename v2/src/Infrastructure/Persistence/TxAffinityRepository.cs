using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Domain.Repositories;
using ConvivenciaPix.V2.Domain.Routing;
using Microsoft.Data.SqlClient;

namespace ConvivenciaPix.V2.Infrastructure.Persistence;

public sealed class TxAffinityRepository(CoexistenceDb db) : ITxAffinityRepository
{
    // Shared with InboundMessageRepository, which binds affinities inside its own transaction.
    internal const string GetOrAddSql = """
        INSERT INTO dbo.SpiTxAffinity (KeyKind, TxKey, SystemName, Origin)
        SELECT @KeyKind, @TxKey, @SystemName, @Origin
        WHERE NOT EXISTS (
            SELECT 1 FROM dbo.SpiTxAffinity WITH (UPDLOCK, HOLDLOCK) WHERE KeyKind = @KeyKind AND TxKey = @TxKey);

        SELECT SystemName FROM dbo.SpiTxAffinity WHERE KeyKind = @KeyKind AND TxKey = @TxKey;
        """;

    internal static SqlCommand GetOrAddCommand(
        SqlConnection connection, SqlTransaction? transaction, TxKey key, SystemName system, string origin) =>
        connection.Command(GetOrAddSql, transaction)
            .VarChar("@KeyKind", key.Kind.ToString(), 8)
            .VarChar("@TxKey", key.Value)
            .VarChar("@SystemName", system.ToString(), 10)
            .VarChar("@Origin", origin, 10);

    public async Task<SystemName?> FindAsync(TxKey key, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var command = connection
            .Command("SELECT SystemName FROM dbo.SpiTxAffinity WHERE KeyKind = @KeyKind AND TxKey = @TxKey;")
            .VarChar("@KeyKind", key.Kind.ToString(), 8)
            .VarChar("@TxKey", key.Value);
        return (await command.ExecuteScalarAsync(cancellationToken)).ToSystemName();
    }

    public async Task<SystemName> GetOrAddAsync(
        TxKey key, SystemName system, string origin, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var command = GetOrAddCommand(connection, null, key, system, origin);
        var owner = (await command.ExecuteScalarAsync(cancellationToken)).ToSystemName();
        return owner ?? throw new InvalidOperationException($"SpiTxAffinity row for {key.Kind}:{key.Value} not found after insert.");
    }

    public async Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var command = connection
            .Command("DELETE TOP (@Batch) FROM dbo.SpiTxAffinity WHERE CreatedAt < @Cutoff;")
            .Int("@Batch", batchSize)
            .DateTime2("@Cutoff", cutoffUtc);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
