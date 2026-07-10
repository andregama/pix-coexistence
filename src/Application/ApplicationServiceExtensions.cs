using ConvivenciaPix.Application.UseCases.CorrelateMessages;
using ConvivenciaPix.Application.UseCases.GeneratePibr002;
using ConvivenciaPix.Application.UseCases.PropagateResponse;
using ConvivenciaPix.Application.UseCases.PullStream;
using ConvivenciaPix.Application.UseCases.ReceiveSpiRequest;
using Microsoft.Extensions.DependencyInjection;

namespace ConvivenciaPix.Application;

public static class ApplicationServiceExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IReceiveSpiRequestUseCase, ReceiveSpiRequestUseCase>();
        services.AddScoped<ICorrelateSystemAOutboundUseCase, CorrelateSystemAOutboundUseCase>();
        services.AddScoped<ICorrelateSystemBOutboundUseCase, CorrelateSystemBOutboundUseCase>();
        services.AddScoped<ICorrelateSystemAInboundUseCase, CorrelateSystemAInboundUseCase>();
        services.AddScoped<IGeneratePibr002UseCase, GeneratePibr002UseCase>();
        services.AddScoped<IPropagateResponseUseCase, PropagateResponseUseCase>();
        services.AddScoped<IPullStreamUseCase, PullStreamUseCase>();
        services.AddScoped<IAckStreamUseCase, AckStreamUseCase>();

        return services;
    }
}
