using ConvivenciaPix.Application.UseCases.CorrelateMessages;
using ConvivenciaPix.Application.UseCases.PropagateResponse;
using ConvivenciaPix.Application.UseCases.ReceiveSpiRequest;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace ConvivenciaPix.Application;

public static class ApplicationServiceExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<SpiRequestValidator>();

        services.AddScoped<IReceiveSpiRequestUseCase, ReceiveSpiRequestUseCase>();
        services.AddScoped<ICorrelateMessagesUseCase, CorrelateMessagesUseCase>();
        services.AddScoped<IReceiveSystemBSentUseCase, ReceiveSystemBSentUseCase>();
        services.AddScoped<IPropagateResponseUseCase, PropagateResponseUseCase>();

        return services;
    }
}
