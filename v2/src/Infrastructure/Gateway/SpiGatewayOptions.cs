namespace ConvivenciaPix.V2.Infrastructure.Gateway;

public sealed class SpiGatewayOptions
{
    /// <summary>Base URL of the real SPI (or of the simulator in Development), without the /api path.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// "Http" (plain HttpClient, Development default) or "Dinamo" (mTLS performed by the HSM, default
    /// everywhere else). Empty = pick by environment.
    /// </summary>
    public string Transport { get; set; } = string.Empty;

    /// <summary>
    /// Upper bound of simultaneous forwards through the HSM. Each one holds a thread and an HSM
    /// session for its duration; pull streams are counted separately.
    /// </summary>
    public int MaxConcurrentForwards { get; set; } = 64;

    /// <summary>Id (label) of the mTLS private key stored in the HSM.</summary>
    public string MtlsKeyId { get; set; } = string.Empty;

    /// <summary>Id (label) of the mTLS client certificate stored in the HSM.</summary>
    public string MtlsCertId { get; set; } = string.Empty;

    /// <summary>Id (label) of the chain used to validate the SPI server certificate.</summary>
    public string ServerCertChainId { get; set; } = string.Empty;

    public bool UseGzip { get; set; } = true;
    public bool VerifyHostName { get; set; } = true;
}

/// <summary>
/// Headers that never cross the proxy verbatim: connection-specific ones, and the ones each
/// transport recomputes for the body it actually sends.
/// </summary>
public static class SpiForwardHeaders
{
    private static readonly HashSet<string> NotForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "Content-Length", "Content-Encoding", "Content-Type",
        "Accept-Encoding", "Date", "Server"
    };

    /// <summary>Request headers a system may pass on to the SPI: Accept and the SPI's own PI-* headers.</summary>
    public static bool IsForwardableRequestHeader(string name) =>
        name.Equals("Accept", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("PI-", StringComparison.OrdinalIgnoreCase);

    public static bool IsForwardableResponseHeader(string name) => !NotForwarded.Contains(name);
}
