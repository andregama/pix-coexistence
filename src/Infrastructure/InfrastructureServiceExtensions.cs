using Confluent.Kafka;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Domain.Repositories;
using ConvivenciaPix.Infrastructure.Cache;
using ConvivenciaPix.Infrastructure.Certificates;
using ConvivenciaPix.Infrastructure.Hsm;
using ConvivenciaPix.Infrastructure.Jobs;
using ConvivenciaPix.Infrastructure.Messaging;
using ConvivenciaPix.Infrastructure.Metrics;
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
        services.AddDbContext<CoexistenceDbContext>((sp, options) =>
            options.UseSqlServer(
                sp.GetRequiredService<IConfiguration>().GetConnectionString("SqlServer"),
                sql => sql.CommandTimeout(30)));

        services.AddScoped<ISpiSentMsgRepository, SpiSentMsgRepository>();
        services.AddScoped<ISpiPendingSystemBMsgRepository, SpiPendingSystemBMsgRepository>();
        services.AddScoped<ISpiDiscrepancyRepository, SpiDiscrepancyRepository>();

        var spiMetrics = new SpiMetrics();
        services.AddSingleton<ISpiMetrics>(spiMetrics);
        services.AddSingleton(spiMetrics);

        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var cs = sp.GetRequiredService<IConfiguration>().GetConnectionString("Redis")
                ?? throw new InvalidOperationException("Redis connection string is required.");
            return ConnectionMultiplexer.Connect(cs);
        });
        services.AddSingleton<IResponseCache, RedisResponseCache>();

        services.AddSingleton<IXmlSigningService, XmlSigningService>();
        services.AddSingleton<ISpiXmlParser, SpiXmlParser>();

        AddHsmServices(services, configuration, environment);
        AddCertificateValidator(services, configuration, environment);
        AddOrchestratorClient(services, configuration, environment);
        AddKafkaProducer(services);

        // Singleton — KafkaPublisher wraps the singleton IProducer<string,string>.
        services.AddSingleton<IKafkaPublisher, KafkaPublisher>();

        AddCleanupJob(services);

        return services;
    }

    private static void AddHsmServices(
        IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<DinamoOptions>(configuration.GetSection("Dinamo"));

        if (environment.IsDevelopment())
        {
            services.AddSingleton<IHsmService, MockHsmService>();
        }
        else
        {
            // LocalDinamoSdkClient uses .NET BCL — no DinamoAPI.dll needed.
            // For Production with a real HSM: register DinamoNetSdkClient here instead,
            // after adding DinamoAPI.dll as a local assembly reference.
            services.AddSingleton<IDinamoSdkClient, LocalDinamoSdkClient>();
            services.AddSingleton<IHsmService, DinamoHsmService>();
        }
    }

    private static void AddCertificateValidator(
        IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<CertificateValidatorOptions>(
            configuration.GetSection("CertificateValidator"));

        if (environment.IsDevelopment())
            services.AddSingleton<ICertificateValidator, DevCertificateValidator>();
        else
            services.AddSingleton<ICertificateValidator, BacenCertificateValidator>();
    }

    private static void AddOrchestratorClient(
        IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<OrchestratorOptions>(configuration.GetSection("Orchestrator"));

        if (environment.IsDevelopment())
        {
            services.AddSingleton<IOrchestratorClient, StubOrchestratorClient>();
        }
        else
        {
            services.AddHttpClient<IOrchestratorClient, HttpOrchestratorClient>((provider, client) =>
            {
                var options = configuration.GetSection("Orchestrator").Get<OrchestratorOptions>()
                    ?? new OrchestratorOptions();

                if (!string.IsNullOrWhiteSpace(options.BaseUrl))
                    client.BaseAddress = new Uri(options.BaseUrl);

                if (!string.IsNullOrWhiteSpace(options.ApiKey))
                    client.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);

                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            })
            .AddStandardResilienceHandler();
        }
    }

    private static void AddKafkaProducer(IServiceCollection services)
    {
        services.AddSingleton<IProducer<string, string>>(sp =>
        {
            var bootstrapServers = sp.GetRequiredService<IConfiguration>()["Kafka:BootstrapServers"]
                ?? throw new InvalidOperationException("Kafka:BootstrapServers is required.");
            var config = new ProducerConfig
            {
                BootstrapServers = bootstrapServers,
                Acks = Acks.All,
                EnableIdempotence = true,
                MessageSendMaxRetries = 5,
                RetryBackoffMs = 200,
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
