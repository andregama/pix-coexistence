using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Application.Parsing;
using ConvivenciaPix.V2.Domain.Repositories;
using ConvivenciaPix.V2.Infrastructure.Gateway;
using ConvivenciaPix.V2.Infrastructure.Hsm;
using ConvivenciaPix.V2.Infrastructure.Inbound;
using ConvivenciaPix.V2.Infrastructure.Jobs;
using ConvivenciaPix.V2.Infrastructure.Messaging;
using ConvivenciaPix.V2.Infrastructure.Metrics;
using ConvivenciaPix.V2.Infrastructure.Notifications;
using ConvivenciaPix.V2.Infrastructure.Parsing;
using ConvivenciaPix.V2.Infrastructure.Persistence;
using ConvivenciaPix.V2.Infrastructure.Registry;
using ConvivenciaPix.V2.Infrastructure.Signing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;

namespace ConvivenciaPix.V2.Infrastructure;

public static class InfrastructureServiceExtensions
{
    /// <summary>Everything both hosts (API and inbound worker) need.</summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddSingleton(sp => new CoexistenceDb(RequiredConnectionString(sp, "SqlServer")));

        services.AddSingleton<ISentRouteRepository, SentRouteRepository>();
        services.AddSingleton<ITxAffinityRepository, TxAffinityRepository>();
        services.AddSingleton<IInboundMessageRepository, InboundMessageRepository>();
        services.AddSingleton<IDeliveryQueue, DeliveryQueue>();
        services.AddSingleton<IAuditOutboxRepository, AuditOutboxRepository>();

        services.Configure<RegistryOptions>(configuration.GetSection("Registry"));
        services.AddSingleton<ISystemBDocumentRepository, SystemBDocumentRepository>();
        services.AddSingleton<ISystemBDocumentRegistry, SystemBDocumentRegistry>();
        services.AddHostedService<RegistryRefreshService>();

        services.AddSingleton<ISpiMessageParser, SpiMessageParser>();
        services.AddSingleton<IInboundMessageSplitter, SpiMessageSplitter>();

        services.AddSingleton<ProxyMetrics>();
        services.AddSingleton<IProxyMetrics>(sp => sp.GetRequiredService<ProxyMetrics>());

        services.AddSingleton<IDeliveryNotifier>(sp =>
        {
            var redis = sp.GetRequiredService<IConfiguration>().GetConnectionString("Redis");
            return string.IsNullOrWhiteSpace(redis)
                ? new InMemoryDeliveryNotifier()
                : new RedisDeliveryNotifier(redis, sp.GetRequiredService<ILogger<RedisDeliveryNotifier>>());
        });

        AddSpiGateway(services, configuration, environment);
        AddSigning(services, configuration, environment);

        return services;
    }

    /// <summary>Background services that must run in exactly one kind of host: the inbound worker.</summary>
    public static IServiceCollection AddInboundWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<InboundPullOptions>(configuration.GetSection("InboundPull"));
        services.Configure<AuditOptions>(configuration.GetSection("Audit"));
        services.Configure<CleanupOptions>(configuration.GetSection("Cleanup"));

        services.AddHostedService<BacenPullService>();
        services.AddHostedService<OutboxPublisherService>();
        services.AddHostedService<CleanupService>();

        return services;
    }

    private static void AddSpiGateway(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<SpiGatewayOptions>(configuration.GetSection("SpiGateway"));
        services.Configure<DinamoOptions>(configuration.GetSection("Dinamo"));

        var transport = configuration["SpiGateway:Transport"];
        var useHttp = string.IsNullOrWhiteSpace(transport)
            ? environment.IsDevelopment()
            : transport.Equals("Http", StringComparison.OrdinalIgnoreCase);

        if (useHttp)
        {
            // Local simulator over plain HTTP — no HSM, no client certificate. The gateway applies
            // its own per-request timeout, and nothing is retried.
            services.AddHttpClient(HttpSpiGateway.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip
                });
            services.AddSingleton<ISpiGateway, HttpSpiGateway>();
            return;
        }

        AddDinamoSdkClient(services, environment);
        services.AddSingleton<ISpiGateway, DinamoSpiGateway>();
    }

    private static void AddSigning(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<InboundSigningOptions>(configuration.GetSection("InboundSigning"));

        if (environment.IsDevelopment())
        {
            services.AddSingleton<ISpiMessageSigner, MockSpiMessageSigner>();
            return;
        }

        AddDinamoSdkClient(services, environment);
        services.AddSingleton<ISpiMessageSigner, DinamoSpiMessageSigner>();
    }

    // The real HSM in Production, a software stand-in (local PFX) elsewhere.
    private static void AddDinamoSdkClient(IServiceCollection services, IHostEnvironment environment)
    {
        if (environment.IsProduction())
            services.TryAddSingleton<IDinamoSdkClient, DinamoNetSdkClient>();
        else
            services.TryAddSingleton<IDinamoSdkClient, LocalDinamoSdkClient>();
    }

    private static string RequiredConnectionString(IServiceProvider services, string name) =>
        services.GetRequiredService<IConfiguration>().GetConnectionString(name)
        ?? throw new InvalidOperationException($"ConnectionStrings:{name} is required.");
}
