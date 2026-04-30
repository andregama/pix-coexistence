using Confluent.Kafka;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Domain.Repositories;
using ConvivenciaPix.Infrastructure.Cache;
using ConvivenciaPix.Infrastructure.Jobs;
using ConvivenciaPix.Infrastructure.Messaging;
using ConvivenciaPix.Infrastructure.Orchestrator;
using ConvivenciaPix.Infrastructure.Parsing;
using ConvivenciaPix.Infrastructure.Persistence;
using ConvivenciaPix.Infrastructure.Persistence.Repositories;
using ConvivenciaPix.Infrastructure.Signing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using StackExchange.Redis;

namespace ConvivenciaPix.Infrastructure;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddDbContext<CoexistenceDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("SqlServer"),
                sql => sql.CommandTimeout(30)));

        services.AddScoped<ISpiSentMsgRepository, SpiSentMsgRepository>();
        services.AddScoped<ISpiPendingSystemBMsgRepository, SpiPendingSystemBMsgRepository>();

        var redisConnection = configuration.GetConnectionString("Redis")
            ?? throw new InvalidOperationException("Redis connection string is required.");

        services.AddSingleton<IConnectionMultiplexer>(
            ConnectionMultiplexer.Connect(redisConnection));
        services.AddSingleton<IResponseCache, RedisResponseCache>();

        services.AddSingleton<IXmlSigningService, XmlSigningService>();
        services.AddSingleton<ISpiXmlParser, SpiXmlParser>();
        services.AddSingleton<IOrchestratorClient, StubOrchestratorClient>();

        if (environment.IsDevelopment())
            services.AddSingleton<IHsmService, MockHsmService>();
        // Production: register real Dinamo HSM adapter here

        AddKafkaProducer(services, configuration);
        // Singleton — KafkaPublisher wraps the singleton IProducer<string,string>.
        // Scoped lifetime was a bug: scope disposal would dispose the shared producer.
        services.AddSingleton<IKafkaPublisher, KafkaPublisher>();

        AddCleanupJob(services);

        return services;
    }

    private static void AddKafkaProducer(IServiceCollection services, IConfiguration configuration)
    {
        var bootstrapServers = configuration["Kafka:BootstrapServers"]
            ?? throw new InvalidOperationException("Kafka:BootstrapServers is required.");

        services.AddSingleton(_ =>
        {
            var config = new ProducerConfig
            {
                BootstrapServers = bootstrapServers,
                Acks = Acks.All,
                EnableIdempotence = true,
                MessageSendMaxRetries = 5,
                RetryBackoffMs = 200,
                // Prevents message loss when broker is temporarily unavailable
                MessageTimeoutMs = 30_000,
            };
            return new ProducerBuilder<string, string>(config).Build();
        });
    }

    private static void AddCleanupJob(IServiceCollection services)
    {
        services.AddQuartz(q =>
        {
            var jobKey = new JobKey("SpiSentMsgCleanup");
            q.AddJob<SpiSentMsgCleanupJob>(opts => opts.WithIdentity(jobKey));
            q.AddTrigger(opts => opts
                .ForJob(jobKey)
                .WithIdentity("SpiSentMsgCleanup-trigger")
                // Daily at 02:00 UTC
                .WithCronSchedule("0 0 2 * * ?"));
        });

        services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);
    }
}
