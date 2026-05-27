using Confluent.Kafka;
using ConvivenciaPix.Application;
using ConvivenciaPix.Infrastructure;
using ConvivenciaPix.Infrastructure.Persistence;
using ConvivenciaPix.SpiCorrelateWorker.Consumers;
using ConvivenciaPix.SpiProxyWorker.Consumers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Testcontainers.Kafka;
using Testcontainers.MsSql;
using Testcontainers.Redis;
using Xunit;

namespace ConvivenciaPix.Integration.Tests;

public sealed class IntegrationTestFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _sqlContainer = new MsSqlBuilder().Build();
    private readonly RedisContainer _redisContainer = new RedisBuilder().Build();
    private readonly KafkaContainer _kafkaContainer = new KafkaBuilder()
        .WithImage("confluentinc/cp-kafka:7.6.1")
        .Build();

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
                ["ProxyApi:Outbound:LongPollSeconds"] = "2",
                ["ProxyApi:Outbound:StreamTtlMinutes"] = "10",
                ["ProxyApi:Outbound:MaxMessagesPerMultipart"] = "10"
            });
        });

        builder.ConfigureServices((context, services) =>
        {
            // Bypass certificate authentication — TestServer has no TLS layer
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            });
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, _ => { });

            // Replace the idempotent producer with a simpler one — idempotence requires
            // broker PID negotiation which stalls for 30s on a freshly-started container.
            services.AddSingleton<IProducer<string, string>>(sp =>
            {
                var bootstrapServers = sp.GetRequiredService<IConfiguration>()["Kafka:BootstrapServers"]!;
                return new ProducerBuilder<string, string>(new ProducerConfig
                {
                    BootstrapServers = bootstrapServers,
                    Acks = Acks.Leader,
                    MessageTimeoutMs = 5_000,
                }).Build();
            });

            // Register workers as hosted services so they run during the test
            services.AddHostedService<SystemBSentConsumer>();
            services.AddHostedService<SystemAResponseCorrelateConsumer>();
            services.AddHostedService<SystemAResponseProxyConsumer>();
        });
    }
}

internal sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "IntegrationTest";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Name, "system-b")], SchemeName));
        return Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
