using ConvivenciaPix.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace ConvivenciaPix.Infrastructure.Tests.Fixtures;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder().Build();

    public string ConnectionString { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        await using var ctx = CreateDbContext();
        await ctx.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public CoexistenceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CoexistenceDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
        return new CoexistenceDbContext(options);
    }
}
