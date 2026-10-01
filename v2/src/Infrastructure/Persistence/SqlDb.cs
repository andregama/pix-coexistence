using ConvivenciaPix.V2.Domain;
using Microsoft.Data.SqlClient;
using System.Data;

namespace ConvivenciaPix.V2.Infrastructure.Persistence;

/// <summary>Opens connections to one SQL Server database.</summary>
public abstract class SqlDb(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

/// <summary>The proxy's own database (<c>DB_COEXISTENCE_V2</c>).</summary>
public sealed class CoexistenceDb(string connectionString) : SqlDb(connectionString);

/// <summary>The existing database that holds <c>TB_DADOS_CONVIVENCIA</c>.</summary>
public sealed class RegistryDb(string connectionString) : SqlDb(connectionString);

internal static class SqlExtensions
{
    // Unique-key violations: 2627 (constraint) and 2601 (unique index).
    public static bool IsDuplicateKey(this SqlException ex) => ex.Number is 2627 or 2601;

    public static SqlCommand Command(this SqlConnection connection, string sql, SqlTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    // Identifier columns are VARCHAR with a binary collation; typed parameters keep index seeks
    // (an NVARCHAR parameter would force a conversion of the column).
    public static SqlCommand VarChar(this SqlCommand command, string name, string? value, int size = 64)
    {
        command.Parameters.Add(name, SqlDbType.VarChar, size).Value = (object?)value ?? DBNull.Value;
        return command;
    }

    public static SqlCommand Int(this SqlCommand command, string name, int value)
    {
        command.Parameters.Add(name, SqlDbType.Int).Value = value;
        return command;
    }

    public static SqlCommand DateTime2(this SqlCommand command, string name, DateTime value)
    {
        command.Parameters.Add(name, SqlDbType.DateTime2).Value = value;
        return command;
    }

    public static SystemName? ToSystemName(this object? scalar) =>
        scalar is string text ? Enum.Parse<SystemName>(text) : null;
}
