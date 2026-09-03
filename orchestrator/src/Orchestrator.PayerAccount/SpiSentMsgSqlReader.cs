using Microsoft.Data.SqlClient;

namespace Orchestrator.PayerAccount;

/// <summary>
/// ADO.NET reader for <c>dbo.SpiSentMsg</c>. Read-only; opens a short-lived connection per query.
/// The connection string points at the coexistence database (DB_COEXISTENCE).
/// </summary>
public sealed class SpiSentMsgSqlReader : ISpiSentMsgReader
{
    private const string Query =
        "SELECT MsgType, XmlMsgSystemA, XmlMsgSystemB, OriginalMsgIdempotentId " +
        "FROM dbo.SpiSentMsg WHERE IdempotentId = @id";

    private readonly string _connectionString;

    public SpiSentMsgSqlReader(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<SpiSentMsgRow?> FindByIdempotentIdAsync(
        string idempotentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotentId);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(Query, connection);
        command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.VarChar, 255) { Value = idempotentId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new SpiSentMsgRow(
            IdempotentId: idempotentId,
            MsgType: reader.GetString(0),
            XmlMsgSystemA: reader.IsDBNull(1) ? null : reader.GetString(1),
            XmlMsgSystemB: reader.IsDBNull(2) ? null : reader.GetString(2),
            OriginalMsgIdempotentId: reader.IsDBNull(3) ? null : reader.GetString(3));
    }
}
