using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Domain.Repositories;
using ConvivenciaPix.V2.Domain.Routing;
using Microsoft.Extensions.Options;

namespace ConvivenciaPix.V2.Application.Routing;

/// <summary>A response goes back to the system that sent the message it refers to.</summary>
public sealed class SentByOriginalMsgIdResolver(ISentRouteRepository sentRoutes) : IRouteResolver
{
    public string Name => RouteSources.SentByOriginalMsgId;

    public async Task<RoutingDecision?> ResolveAsync(RouteContext context, CancellationToken cancellationToken)
    {
        foreach (var reference in context.Message.OriginalMsgRefs)
        {
            var system = await sentRoutes.FindSystemByMsgIdAsync(reference, cancellationToken);
            if (system is not null)
                return RoutingDecision.To(system.Value, Name);
        }
        return null;
    }
}

/// <summary>A rejection that refers to the PI-ResourceId Bacen assigned to a sent message.</summary>
public sealed class SentByPiResourceIdResolver(ISentRouteRepository sentRoutes) : IRouteResolver
{
    public string Name => RouteSources.SentByPiResourceId;

    public async Task<RoutingDecision?> ResolveAsync(RouteContext context, CancellationToken cancellationToken)
    {
        foreach (var reference in context.Message.OriginalMsgRefs)
        {
            var system = await sentRoutes.FindSystemByPiResourceIdAsync(reference, cancellationToken);
            if (system is not null)
                return RoutingDecision.To(system.Value, Name);
        }
        return null;
    }
}

/// <summary>A message follows the system already handling its transaction (sent or received).</summary>
public sealed class TxAffinityResolver(ITxAffinityRepository affinities) : IRouteResolver
{
    public string Name => RouteSources.TxAffinity;

    public async Task<RoutingDecision?> ResolveAsync(RouteContext context, CancellationToken cancellationToken)
    {
        foreach (var key in context.Transaction.TxKeys)
        {
            var system = await affinities.FindAsync(key, cancellationToken);
            if (system is not null)
                return RoutingDecision.To(system.Value, Name);
        }
        return null;
    }
}

/// <summary>A customer registered for System B is served by System B; everyone else by System A.</summary>
public sealed class DocumentRegistryResolver(ISystemBDocumentRegistry registry) : IRouteResolver
{
    public string Name => RouteSources.DocumentRegistry;

    public Task<RoutingDecision?> ResolveAsync(RouteContext context, CancellationToken cancellationToken)
    {
        var transaction = context.Transaction;
        var document = context.Rule.DocumentParty switch
        {
            DocumentParty.Creditor => transaction.CreditorDocument,
            DocumentParty.Debtor => transaction.DebtorDocument,
            _ => transaction.OwnSideDocument
        };

        if (string.IsNullOrEmpty(document))
            return Task.FromResult<RoutingDecision?>(null);

        var system = registry.Contains(document) ? Domain.SystemName.SystemB : Domain.SystemName.SystemA;
        return Task.FromResult<RoutingDecision?>(RoutingDecision.To(system, Name));
    }
}

/// <summary>A pibr.002 goes to the system that sent the same content in a pibr.001.</summary>
public sealed class EchoDataResolver(ISentRouteRepository sentRoutes) : IRouteResolver
{
    public string Name => RouteSources.EchoData;

    public async Task<RoutingDecision?> ResolveAsync(RouteContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(context.Message.EchoData))
            return null;

        var system = await sentRoutes.ClaimEchoAsync(EchoHash.Compute(context.Message.EchoData), cancellationToken);
        return system is null ? null : RoutingDecision.To(system.Value, Name);
    }
}

public sealed class BroadcastResolver : IRouteResolver
{
    public string Name => RouteSources.Broadcast;

    public Task<RoutingDecision?> ResolveAsync(RouteContext context, CancellationToken cancellationToken) =>
        Task.FromResult<RoutingDecision?>(RoutingDecision.ToBoth(Name));
}

public sealed class DefaultResolver(IOptions<RoutingOptions> options) : IRouteResolver
{
    public string Name => RouteSources.Default;

    public Task<RoutingDecision?> ResolveAsync(RouteContext context, CancellationToken cancellationToken) =>
        Task.FromResult<RoutingDecision?>(RoutingDecision.To(options.Value.DefaultSystem, Name));
}
