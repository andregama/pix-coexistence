using ConvivenciaPix.V2.Application.Parsing;
using ConvivenciaPix.V2.Domain.Routing;

namespace ConvivenciaPix.V2.Application.Routing;

/// <summary>One transaction of a message being routed, with the rule of its message type.</summary>
public sealed record RouteContext(ParsedSpiMessage Message, ParsedTransaction Transaction, RoutingRule Rule);

/// <summary>One routing strategy. Returns null when it cannot decide, so the chain moves on.</summary>
public interface IRouteResolver
{
    /// <summary>Name used in routing chains and persisted as the route source.</summary>
    string Name { get; }

    Task<RoutingDecision?> ResolveAsync(RouteContext context, CancellationToken cancellationToken);
}
