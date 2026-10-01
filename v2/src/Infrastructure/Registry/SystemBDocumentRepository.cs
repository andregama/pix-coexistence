using ConvivenciaPix.V2.Domain.Repositories;
using ConvivenciaPix.V2.Domain.Routing;
using ConvivenciaPix.V2.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ConvivenciaPix.V2.Infrastructure.Registry;

/// <summary>
/// Reads and maintains <c>TB_DADOS_CONVIVENCIA</c>, which lives in the database of the first
/// coexistence solution. This proxy does not own the table and never creates or alters it.
/// </summary>
public sealed class SystemBDocumentRepository(RegistryDb db) : ISystemBDocumentRepository
{
    // SQL-side equivalent of CustomerDocument.Normalize for the punctuation found in CPF/CNPJ.
    private const string NormalizedDocumento =
        "REPLACE(REPLACE(REPLACE(REPLACE(UPPER(Documento), '.', ''), '-', ''), '/', ''), ' ', '')";

    public async Task<IReadOnlyCollection<string>> LoadAllDocumentsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var command = connection.Command("SELECT Documento FROM dbo.TB_DADOS_CONVIVENCIA;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var documents = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var normalized = CustomerDocument.Normalize(reader.GetString(0));
            if (normalized.Length > 0)
                documents.Add(normalized);
        }
        return documents;
    }

    public async Task<IReadOnlyList<SystemBDocumentEntry>> ListAsync(
        string? documento, int skip, int take, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT Id, Conta, Agencia, Documento, DataCriacao
            FROM dbo.TB_DADOS_CONVIVENCIA
            WHERE @Documento IS NULL OR {NormalizedDocumento} = @Documento
            ORDER BY Id
            OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
            """;

        await using var connection = await db.OpenAsync(cancellationToken);
        await using var command = connection.Command(sql)
            .VarChar("@Documento", documento, 50)
            .Int("@Skip", skip)
            .Int("@Take", take);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var entries = new List<SystemBDocumentEntry>();
        while (await reader.ReadAsync(cancellationToken))
            entries.Add(Read(reader));
        return entries;
    }

    public async Task<SystemBDocumentEntry> AddAsync(
        string conta, string agencia, string documento, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO dbo.TB_DADOS_CONVIVENCIA (Conta, Agencia, Documento, DataCriacao)
            OUTPUT inserted.Id, inserted.Conta, inserted.Agencia, inserted.Documento, inserted.DataCriacao
            VALUES (@Conta, @Agencia, @Documento, SYSUTCDATETIME());
            """;

        await using var connection = await db.OpenAsync(cancellationToken);
        await using var command = connection.Command(sql)
            .VarChar("@Conta", conta, 50)
            .VarChar("@Agencia", agencia, 50)
            .VarChar("@Documento", documento, 50);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return Read(reader);
    }

    public async Task<int> RemoveAsync(string documento, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var command = connection
            .Command($"DELETE FROM dbo.TB_DADOS_CONVIVENCIA WHERE {NormalizedDocumento} = @Documento;")
            .VarChar("@Documento", CustomerDocument.Normalize(documento), 50);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SystemBDocumentEntry Read(SqlDataReader reader) =>
        new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDateTime(4));
}
