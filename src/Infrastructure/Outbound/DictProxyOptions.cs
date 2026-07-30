namespace ConvivenciaPix.Infrastructure.Outbound;

/// <summary>
/// Configuration for the DICT proxy's outbound call to the real Bacen DICT API. Bound from the
/// <c>DictProxy</c> configuration section. Certificate settings are consumed by the environment-
/// switched <see cref="Certificates.IDictClientCertificateProvider"/>: Production uses
/// <see cref="ClientCertThumbprint"/> (HSM-backed CNG key in the Windows store); Dev/Staging use
/// <see cref="ClientCertPfxPath"/> + <see cref="ClientCertPfxPassword"/>.
/// </summary>
public sealed class DictProxyOptions
{
    /// <summary>Base URL of the real DICT API. Relative request paths are appended to this.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Per-request timeout for the outbound DICT call (RF-09 default 30s).</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Thumbprint of the HSM-backed client certificate in the machine store (Production).</summary>
    public string ClientCertThumbprint { get; set; } = string.Empty;

    /// <summary>Path to the client-certificate PFX used for outbound mTLS (Dev/Staging).</summary>
    public string ClientCertPfxPath { get; set; } = string.Empty;

    /// <summary>Password for <see cref="ClientCertPfxPath"/> (Dev/Staging; may be empty).</summary>
    public string ClientCertPfxPassword { get; set; } = string.Empty;
}
