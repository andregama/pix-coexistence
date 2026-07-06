using Confluent.Kafka;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Domain.Repositories;
using ConvivenciaPix.Infrastructure.Cache;
using ConvivenciaPix.Infrastructure.Certificates;
using ConvivenciaPix.Infrastructure.Hsm;
using ConvivenciaPix.Infrastructure.Jobs;
using ConvivenciaPix.Infrastructure.Messaging;
using ConvivenciaPix.Infrastructure.Metrics;
using ConvivenciaPix.Infrastructure.Outbound;
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
        services.AddScoped<ISpiReceivedMsgRepository, SpiReceivedMsgRepository>();
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
        services.AddSingleton<IOutboundStream, RedisOutboundStream>();

        services.AddSingleton<IXmlSigningService, XmlSigningService>();
        services.AddSingleton<ISpiXmlParser, SpiXmlParser>();

        services.Configure<ResponseTransformOptions>(configuration.GetSection("ResponseTransform"));
        services.AddSingleton<IInboundResponseTransformer, InboundResponseTransformer>();

        AddHsmServices(services, configuration, environment);
        AddCertificateValidator(services, configuration, environment);
        AddKafkaProducer(services);

        services.AddSingleton<IKafkaPublisher, KafkaPublisher>();

        AddCleanupJobs(services);

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

    private static void AddCleanupJobs(IServiceCollection services)
    {
        services.AddQuartz(q =>
        {
            var sentMsgJobKey = new JobKey("SpiSentMsgCleanup");
            q.AddJob<SpiSentMsgCleanupJob>(opts => opts.WithIdentity(sentMsgJobKey));
            q.AddTrigger(opts => opts
                .ForJob(sentMsgJobKey)
                .WithIdentity("SpiSentMsgCleanup-trigger")
                .WithCronSchedule("0 0 2 * * ?"));

            var receivedMsgJobKey = new JobKey("SpiReceivedMsgCleanup");
            q.AddJob<SpiReceivedMsgCleanupJob>(opts => opts.WithIdentity(receivedMsgJobKey));
            q.AddTrigger(opts => opts
                .ForJob(receivedMsgJobKey)
                .WithIdentity("SpiReceivedMsgCleanup-trigger")
                .WithCronSchedule("0 30 2 * * ?"));
        });

        services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);
    }
}
