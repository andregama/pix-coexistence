using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Domain.Routing;

namespace ConvivenciaPix.V2.Application.Routing;

/// <summary>Which party of an inbound transaction is the bank's own customer.</summary>
public enum DocumentParty
{
    Creditor,
    Debtor,

    /// <summary>
    /// The party whose agent is the bank the message is addressed to. For message types that travel
    /// in both directions (the bank may be on either side of the mandate or payment).
    /// </summary>
    OwnSide
}

/// <summary>What to do with a message whose transactions belong to different systems.</summary>
public enum MixedOwnersPolicy
{
    /// <summary>
    /// Each system receives its own copy with only its transactions, signed by the bank's HSM.
    /// </summary>
    Split,

    /// <summary>
    /// Deliver the whole message to every system that owns one of its transactions; each system
    /// must ignore the transactions it does not know.
    /// </summary>
    AllOwners,

    /// <summary>
    /// Deliver the whole message to the default system only, which then owns every transaction in it.
    /// </summary>
    DefaultSystem
}

public sealed class RoutingRule
{
    /// <summary>Resolver names (see <see cref="RouteSources"/>) tried in order.</summary>
    public List<string> Chain { get; set; } = [];

    public DocumentParty DocumentParty { get; set; } = DocumentParty.Creditor;

    public MixedOwnersPolicy WhenOwnersDiffer { get; set; } = MixedOwnersPolicy.Split;
}

/// <summary>
/// Bound from the <c>Routing</c> section. <see cref="Rules"/> only needs the message types whose
/// behaviour differs from <see cref="RoutingTable.Defaults"/>; a key is either an exact type
/// ("pacs.008") or a family wildcard ("reda.*").
/// </summary>
public sealed class RoutingOptions
{
    /// <summary>System that receives a message nothing else could route (pre-cut-over traffic).</summary>
    public SystemName DefaultSystem { get; set; } = SystemName.SystemA;

    public Dictionary<string, RoutingRule> Rules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Chain for message types with no rule. Empty = built-in default.</summary>
    public List<string> UnknownTypeChain { get; set; } = [];
}
