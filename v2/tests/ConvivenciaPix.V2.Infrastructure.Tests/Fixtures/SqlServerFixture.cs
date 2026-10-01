using ConvivenciaPix.V2.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace ConvivenciaPix.V2.Infrastructure.Tests.Fixtures;

/// <summary>
/// A throwaway SQL Server with the v2 schema applied from <c>v2/infra/sql</c>, plus a copy of the
/// registry table (which in real environments lives in another database and is not ours to create).
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string RegistryTableSql = """
        IF OBJECT_ID('dbo.TB_DADOS_CONVIVENCIA', 'U') IS NULL
        CREATE TABLE dbo.TB_DADOS_CONVIVENCIA
        (
            Id          INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
            Conta       VARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            Agencia     VARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            Documento   VARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            DataCriacao DATETIME2 NOT NULL
        );
        """;

    private readonly MsSqlContainer _container = new MsSqlBuilder().Build();

    public string ConnectionString { get; private set; } = default!;

    public CoexistenceDb Db => new(ConnectionString);

    public RegistryDb RegistryDb => new(ConnectionString);

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        await SqlSchemaBootstrapper.ApplyAsync(ConnectionString);
        await ExecuteAsync(RegistryTableSql);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
