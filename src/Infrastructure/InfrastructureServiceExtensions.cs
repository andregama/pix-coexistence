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
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
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
        services.Configure<Analytics.AnalyticsOptions>(configuration.GetSection("Analytics"));
        services.AddScoped<ICoexistenceAnalyticsReader, Analytics.CoexistenceAnalyticsReader>();

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

        services.AddSingleton<ISpiXmlParser, SpiXmlParser>();

        services.Configure<ResponseTransformOptions>(configuration.GetSection("ResponseTransform"));
        services.AddSingleton<IInboundResponseTransformer, InboundResponseTransformer>();
        services.AddSingleton<IPibr002Builder, Pibr002Builder>();

        AddHsmServices(services, configuration, environment);
        AddCertificateValidator(services, configuration, environment);
        AddDictProxyServices(services, configuration, environment);
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
            // Production talks to the real HSM (native lib); Staging uses the software PFX
            // simulation. Both go through DinamoHsmService via the IDinamoSdkClient contract.
            if (environment.IsProduction())
                services.AddSingleton<IDinamoSdkClient, DinamoNetSdkClient>();
            else
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

    /// <summary>
    /// Lean registration for the DICT proxy API. Unlike <see cref="AddInfrastructure"/>, it wires no
    /// database, Redis, Kafka, or scheduled jobs — the DICT proxy is a stateless signing forward
    /// proxy. It registers only metrics, the HSM signing services, the inbound certificate validator
    /// (for mTLS client-cert auth), and the outbound DICT forwarder + client-certificate provider.
    /// </summary>
    public static IServiceCollection AddDictInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var spiMetrics = new SpiMetrics();
        services.AddSingleton<ISpiMetrics>(spiMetrics);
        services.AddSingleton(spiMetrics);

        AddHsmServices(services, configuration, environment);
        AddCertificateValidator(services, configuration, environment);
        AddDictProxyServices(services, configuration, environment);

        return services;
    }

    private static void AddDictProxyServices(
        IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<DictProxyOptions>(configuration.GetSection("DictProxy"));

        if (environment.IsDevelopment())
        {
            // Development targets a local stub (e.g. WireMock) over plain HTTP — no HSM, no client cert.
            services.AddHttpClient<IDictForwarder, HttpDictForwarder>((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<DictProxyOptions>>().Value;
                if (string.IsNullOrWhiteSpace(options.BaseUrl))
                    throw new InvalidOperationException("DictProxy:BaseUrl is required for the DICT proxy.");

                var baseUrl = options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/";
                client.BaseAddress = new Uri(baseUrl);
                client.Timeout = Timeout.InfiniteTimeSpan; // resilience handler owns timeouts
            })
            .AddStandardResilienceHandler(resilience =>
            {
                var timeoutSeconds = configuration.GetValue("DictProxy:TimeoutSeconds", 30);
                var total = TimeSpan.FromSeconds(timeoutSeconds);
                resilience.TotalRequestTimeout.Timeout = total;
                if (resilience.AttemptTimeout.Timeout > total)
                    resilience.AttemptTimeout.Timeout = total;
                var minSampling = resilience.AttemptTimeout.Timeout * 2;
                if (resilience.CircuitBreaker.SamplingDuration < minSampling)
                    resilience.CircuitBreaker.SamplingDuration = minSampling;
            });

            return;
        }

        // Production/Staging: the HSM performs the outbound mTLS request (Dinamo PIX). IDinamoSdkClient
        // is already registered by AddHsmServices (Net in Production, Local in Staging). RF-09 retry +
        // timeout are provided by a Polly pipeline, since there is no HttpClient transport to attach to.
        var timeoutSeconds = configuration.GetValue("DictProxy:TimeoutSeconds", 30);
        services.AddKeyedSingleton<ResiliencePipeline>(DictResilience.Key, (_, _) =>
            new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = TimeSpan.FromMilliseconds(200)
                })
                .AddTimeout(TimeSpan.FromSeconds(timeoutSeconds))
                .Build());

        services.AddSingleton<IDictForwarder, DinamoDictForwarder>();
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
