namespace ConvivenciaPix.V2.SpiProxyApi.Listeners;

/// <summary>Which listener a request arrived on. The listener is what identifies the calling system.</summary>
public enum ListenerKind
{
    Unknown,
    SystemA,
    SystemB,
    Admin
}

public sealed class ListenerTrustOptions
{
    /// <summary>Thumbprints of the client certificates accepted on this listener.</summary>
    public string[] TrustedThumbprints { get; set; } = [];
}

public sealed class ListenerCertificateOptions
{
    public string Path { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Bound from <c>Listeners</c>. Each system reaches the proxy on its own port, which is how the
/// proxy knows who is calling; a third port serves administration, metrics and documentation.
/// </summary>
public sealed class ListenerOptions
{
    public int SystemAPort { get; set; } = 5201;
    public int SystemBPort { get; set; } = 5202;
    public int AdminPort { get; set; } = 5203;

    /// <summary>TLS with a required client certificate. Null = on everywhere except Development.</summary>
    public bool? Https { get; set; }

    /// <summary>Server certificate (PFX) presented on every listener when HTTPS is on.</summary>
    public ListenerCertificateOptions Certificate { get; set; } = new();

    public ListenerTrustOptions SystemA { get; set; } = new();
    public ListenerTrustOptions SystemB { get; set; } = new();
    public ListenerTrustOptions Admin { get; set; } = new();

    public ListenerKind KindOf(int localPort) =>
        localPort == SystemAPort ? ListenerKind.SystemA
        : localPort == SystemBPort ? ListenerKind.SystemB
        : localPort == AdminPort ? ListenerKind.Admin
        : ListenerKind.Unknown;

    public string[] TrustedThumbprints(ListenerKind kind) => kind switch
    {
        ListenerKind.SystemA => SystemA.TrustedThumbprints,
        ListenerKind.SystemB => SystemB.TrustedThumbprints,
        ListenerKind.Admin => Admin.TrustedThumbprints,
        _ => []
    };
}
