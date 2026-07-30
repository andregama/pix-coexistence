using ConvivenciaPix.Application.UseCases.CorrelateMessages;
using ConvivenciaPix.Application.UseCases.GeneratePibr002;
using ConvivenciaPix.Application.UseCases.PropagateResponse;
using ConvivenciaPix.Application.UseCases.ProxyDict;
using ConvivenciaPix.Application.UseCases.PullStream;
using ConvivenciaPix.Application.UseCases.ReceiveSpiRequest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConvivenciaPix.Application;

public static class ApplicationServiceExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // System clock, injected wherever delays/timeouts must be abstracted for testing.
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<IReceiveSpiRequestUseCase, ReceiveSpiRequestUseCase>();
        services.AddScoped<ICorrelateSystemAOutboundUseCase, CorrelateSystemAOutboundUseCase>();
        services.AddScoped<ICorrelateSystemBOutboundUseCase, CorrelateSystemBOutboundUseCase>();
        services.AddScoped<ICorrelateSystemAInboundUseCase, CorrelateSystemAInboundUseCase>();
        services.AddScoped<IGeneratePibr002UseCase, GeneratePibr002UseCase>();
        services.AddScoped<IPropagateResponseUseCase, PropagateResponseUseCase>();
        services.AddScoped<IPullStreamUseCase, PullStreamUseCase>();
        services.AddScoped<IAckStreamUseCase, AckStreamUseCase>();
        services.AddScoped<IProxyDictRequestUseCase, ProxyDictRequestUseCase>();

        return services;
    }

    /// <summary>
    /// Lean Application registration for the DICT proxy host — only the DICT use case and the system
    /// clock. Avoids pulling in the SPI use cases, whose Kafka/Redis/DB dependencies the stateless
    /// DICT proxy does not wire.
    /// </summary>
    public static IServiceCollection AddDictApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IProxyDictRequestUseCase, ProxyDictRequestUseCase>();
        return services;
    }
}
