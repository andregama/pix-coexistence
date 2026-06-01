using System.Reflection;
using Microsoft.Data.SqlClient;

namespace ConvivenciaPix.Infrastructure.Persistence;

/// <summary>
/// Applies the schema-creation scripts embedded from <c>infra/sql/*.sql</c>.
/// Scripts are idempotent (IF NOT EXISTS guards) and ordered alphabetically by file name.
/// Used by test fixtures and any caller that needs to provision the schema in code;
/// <c>sqlcmd</c> against the same files is the supported path for local/CI provisioning.
/// </summary>
public static class SqlSchemaBootstrapper
{
    private const string ResourcePrefix = "ConvivenciaPix.Infrastructure.SqlScripts.";

    public static async Task ApplyAsync(string connectionString, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        foreach (var (name, sql) in LoadScripts())
        {
            foreach (var batch in SplitBatches(sql))
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = batch;
                try
                {
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                catch (SqlException ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to apply SQL script '{name}'. Batch:\n{batch}", ex);
                }
            }
        }
    }

    private static IEnumerable<(string Name, string Sql)> LoadScripts()
    {
        var assembly = typeof(SqlSchemaBootstrapper).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                        && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        if (names.Length == 0)
        {
            throw new InvalidOperationException(
                $"No embedded SQL scripts found with prefix '{ResourcePrefix}'. " +
                "Verify the Infrastructure csproj embeds infra/sql/*.sql.");
        }

        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded resource '{name}' not found.");
            using var reader = new StreamReader(stream);
            yield return (name, reader.ReadToEnd());
        }
    }

    // Splits on the sqlcmd batch separator `GO` on its own line. Mirrors how SSMS / sqlcmd
    // chunk a script so the same files can be run with either tool.
    private static IEnumerable<string> SplitBatches(string script)
    {
        var lines = script.Split('\n');
        var current = new System.Text.StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (string.Equals(line.Trim(), "GO", StringComparison.OrdinalIgnoreCase))
            {
                var batch = current.ToString();
                current.Clear();
                if (!string.IsNullOrWhiteSpace(batch))
                    yield return batch;
            }
            else
            {
                current.AppendLine(line);
            }
        }

        var tail = current.ToString();
        if (!string.IsNullOrWhiteSpace(tail))
            yield return tail;
    }
}
