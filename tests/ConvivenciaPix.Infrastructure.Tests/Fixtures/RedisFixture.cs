using StackExchange.Redis;
using Testcontainers.Redis;

namespace ConvivenciaPix.Infrastructure.Tests.Fixtures;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder().Build();

    public IConnectionMultiplexer Connection { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Connection = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await Connection.CloseAsync();
        await _container.DisposeAsync();
    }
}
