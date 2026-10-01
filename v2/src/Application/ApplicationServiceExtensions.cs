using ConvivenciaPix.V2.Application.Routing;
using ConvivenciaPix.V2.Application.UseCases.ForwardMessages;
using ConvivenciaPix.V2.Application.UseCases.ProcessInboundMessage;
using ConvivenciaPix.V2.Application.UseCases.PullStream;
using ConvivenciaPix.V2.Application.UseCases.SystemBDocuments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConvivenciaPix.V2.Application;

public static class ApplicationServiceExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RoutingOptions>(configuration.GetSection("Routing"));
        services.Configure<ForwardOptions>(configuration.GetSection("Forward"));
        services.Configure<DeliveryStreamOptions>(configuration.GetSection("DeliveryStream"));

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<RoutingTable>();
        services.AddSingleton<IRouteResolver, SentByOriginalMsgIdResolver>();
        services.AddSingleton<IRouteResolver, SentByPiResourceIdResolver>();
        services.AddSingleton<IRouteResolver, TxAffinityResolver>();
        services.AddSingleton<IRouteResolver, DocumentRegistryResolver>();
        services.AddSingleton<IRouteResolver, EchoDataResolver>();
        services.AddSingleton<IRouteResolver, BroadcastResolver>();
        services.AddSingleton<IRouteResolver, DefaultResolver>();
        services.AddSingleton<IInboundRouter, InboundRouter>();

        services.AddSingleton<IForwardMessagesUseCase, ForwardMessagesUseCase>();
        services.AddSingleton<IProcessInboundMessageUseCase, ProcessInboundMessageUseCase>();
        services.AddSingleton<IPullStreamUseCase, PullStreamUseCase>();
        services.AddSingleton<IAckStreamUseCase, AckStreamUseCase>();
        services.AddSingleton<ISystemBDocumentsService, SystemBDocumentsService>();

        return services;
    }
}
