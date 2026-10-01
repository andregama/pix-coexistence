using ConvivenciaPix.V2.Domain;
using Microsoft.Extensions.Options;

namespace ConvivenciaPix.V2.SpiProxyApi.Listeners;

/// <summary>
/// Tells which listener a request arrived on. Behind an abstraction so that a deployment that
/// cannot preserve the destination port (or a test server, which has none) can identify the
/// caller differently.
/// </summary>
public interface ICallerSystemResolver
{
    ListenerKind Resolve(HttpContext context);
}

/// <summary>
/// Identifies the listener by the local port of the connection — never by the Host header, which
/// the client controls.
/// </summary>
public sealed class PortCallerSystemResolver(IOptions<ListenerOptions> options) : ICallerSystemResolver
{
    public ListenerKind Resolve(HttpContext context) => options.Value.KindOf(context.Connection.LocalPort);
}

public static class CallerSystemExtensions
{
    private const string ItemKey = "ConvivenciaPix.V2.Listener";

    public static void SetListener(this HttpContext context, ListenerKind kind) => context.Items[ItemKey] = kind;

    public static ListenerKind GetListener(this HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var kind) && kind is ListenerKind listener ? listener : ListenerKind.Unknown;

    /// <summary>The system calling an SPI endpoint. Only valid on the system listeners.</summary>
    public static SystemName GetCallerSystem(this HttpContext context) => context.GetListener() switch
    {
        ListenerKind.SystemA => SystemName.SystemA,
        ListenerKind.SystemB => SystemName.SystemB,
        var other => throw new InvalidOperationException($"No calling system on listener '{other}'.")
    };
}
