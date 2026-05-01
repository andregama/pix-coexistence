using ConvivenciaPix.Application;
using ConvivenciaPix.Infrastructure;
using ConvivenciaPix.Infrastructure.Persistence;
using ConvivenciaPix.SpiCorrelateWorker.Consumers;
using ConvivenciaPix.SpiProxyWorker.Consumers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.Kafka;
using Testcontainers.MsSql;
using Testcontainers.Redis;
using Xunit;

namespace ConvivenciaPix.Integration.Tests;

public sealed class IntegrationTestFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _sqlContainer = new MsSqlBuilder().Build();
    private readonly RedisContainer _redisContainer = new RedisBuilder().Build();
    private readonly KafkaContainer _kafkaContainer = new KafkaBuilder().Build();

    public string SqlConnectionString => _sqlContainer.GetConnectionString();
    public string RedisConnectionString => _redisContainer.GetConnectionString();
    public string KafkaBootstrapServers => _kafkaContainer.GetBootstrapAddress();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(
            _sqlContainer.StartAsync(),
            _redisContainer.StartAsync(),
            _kafkaContainer.StartAsync());

        // Apply migrations
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoexistenceDbContext>();
        await db.Database.MigrateAsync();
    }

    public new async Task DisposeAsync()
    {
        await Task.WhenAll(
            _sqlContainer.DisposeAsync().AsTask(),
            _redisContainer.DisposeAsync().AsTask(),
            _kafkaContainer.DisposeAsync().AsTask());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SqlServer"] = SqlConnectionString,
                ["ConnectionStrings:Redis"] = RedisConnectionString,
                ["Kafka:BootstrapServers"] = KafkaBootstrapServers,
                ["ProxyApi:TimeoutSeconds"] = "10"
            });
        });

        builder.ConfigureServices((context, services) =>
        {
            // Register workers as hosted services so they run during the test
            services.AddHostedService<SystemBSentConsumer>();
            services.AddHostedService<SystemAResponseCorrelateConsumer>();
            services.AddHostedService<SystemAResponseProxyConsumer>();
        });
    }
}
