using ConvivenciaPix.V2.Application.Parsing;
using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Domain.Routing;
using Microsoft.Extensions.Options;

namespace ConvivenciaPix.V2.Application.Routing;

public interface IInboundRouter
{
    Task<RoutingDecision> RouteAsync(ParsedSpiMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs the resolver chain of the message type for each transaction in the message. A message is
/// signed as a whole, so when its transactions belong to different systems the rule's
/// <see cref="MixedOwnersPolicy"/> decides: by default each system gets its own re-signed copy.
/// </summary>
public sealed class InboundRouter : IInboundRouter
{
    private readonly RoutingTable _table;
    private readonly Dictionary<string, IRouteResolver> _resolvers;
    private readonly RoutingOptions _options;

    public InboundRouter(RoutingTable table, IEnumerable<IRouteResolver> resolvers, IOptions<RoutingOptions> options)
    {
        _table = table;
        _options = options.Value;
        _resolvers = resolvers.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);

        var unknown = table.AllRules.SelectMany(r => r.Chain).FirstOrDefault(name => !_resolvers.ContainsKey(name));
        if (unknown is not null)
            throw new InvalidOperationException(
                $"Routing chain references unknown resolver '{unknown}'. Known: {string.Join(", ", _resolvers.Keys)}.");
    }

    public async Task<RoutingDecision> RouteAsync(ParsedSpiMessage message, CancellationToken cancellationToken = default)
    {
        var rule = _table.For(message.MsgType);
        var transactions = message.Transactions.Count > 0 ? message.Transactions : [ParsedTransaction.Empty];

        var decisions = new List<(ParsedTransaction Transaction, RoutingDecision Decision)>(transactions.Count);
        foreach (var transaction in transactions)
            decisions.Add((transaction, await ResolveAsync(new RouteContext(message, transaction, rule), cancellationToken)));

        // Only message types routed by transaction bind their transactions to a system. A report
        // that merely mentions a payment (and is routed to whoever asked for it) must not.
        var binds = rule.Chain.Contains(RouteSources.TxAffinity, StringComparer.OrdinalIgnoreCase);

        var first = decisions[0].Decision;
        if (decisions.All(d => d.Decision.TargetsText == first.TargetsText))
        {
            return first with
            {
                Bindings = binds && first.Targets.Count == 1 ? Bind(message.TxKeys, first.Targets[0]) : []
            };
        }

        if (rule.WhenOwnersDiffer == MixedOwnersPolicy.DefaultSystem)
        {
            // One system receives (and answers) everything, so every transaction follows it.
            return RoutingDecision.To(_options.DefaultSystem, RouteSources.Mixed) with
            {
                Bindings = binds ? Bind(message.TxKeys, _options.DefaultSystem) : []
            };
        }

        var owners = decisions.SelectMany(d => d.Decision.Targets).Distinct().Order().ToList();
        var bindings = binds
            ? decisions
                .Where(d => d.Decision.Targets.Count == 1)
                .SelectMany(d => Bind(d.Transaction.TxKeys, d.Decision.Targets[0]))
                .ToList()
            : [];

        if (rule.WhenOwnersDiffer == MixedOwnersPolicy.AllOwners)
            return new RoutingDecision(owners, RouteSources.Mixed) { Bindings = bindings };

        // Split: each owner gets the transactions decided for it (one decided for both goes to both).
        var parts = owners
            .Select(owner => new RoutingPart(
                owner,
                Enumerable.Range(0, decisions.Count).Where(i => decisions[i].Decision.Targets.Contains(owner)).ToList()))
            .ToList();
        return new RoutingDecision(owners, RouteSources.Split) { Bindings = bindings, Parts = parts };
    }

    private async Task<RoutingDecision> ResolveAsync(RouteContext context, CancellationToken cancellationToken)
    {
        foreach (var name in context.Rule.Chain)
        {
            var decision = await _resolvers[name].ResolveAsync(context, cancellationToken);
            if (decision is not null)
                return decision;
        }

        // A chain that ends without a decision still has to deliver the message somewhere.
        return RoutingDecision.To(_options.DefaultSystem, RouteSources.Default);
    }

    private static List<TxBinding> Bind(IEnumerable<TxKey> keys, SystemName system) =>
        keys.Select(key => new TxBinding(key, system)).ToList();
}
