namespace ConvivenciaPix.Infrastructure.Outbound;

/// <summary>
/// Shared header rules for the DICT forwarders. Hop-by-hop and length/host headers are connection-
/// specific or recomputed by the transport, so they are never forwarded verbatim in either direction.
/// </summary>
internal static class DictForwardHeaders
{
    public static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Content-Length"
    };

    public static bool IsHopByHop(string name) => HopByHop.Contains(name);
}
