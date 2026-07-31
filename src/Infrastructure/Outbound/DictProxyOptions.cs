namespace ConvivenciaPix.Infrastructure.Outbound;

/// <summary>
/// Configuration for the DICT proxy's outbound call to the real Bacen DICT API. Bound from the
/// <c>DictProxy</c> configuration section. In Production/Staging the request goes through the HSM
/// (Dinamo PIX HTTP), which performs mTLS using the client key/cert referenced by
/// <see cref="MtlsKeyId"/>/<see cref="MtlsCertId"/> and validates the server against
/// <see cref="ServerCertChainId"/> — no certificate is handled in application code. In Development a
/// plain HttpClient targets a local stub and these HSM label fields are unused.
/// </summary>
public sealed class DictProxyOptions
{
    /// <summary>Base URL of the real DICT API. Relative request paths are appended to this.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Per-request timeout for the outbound DICT call (RF-09 default 30s).</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>HSM label of the private key used for the outbound mTLS client certificate.</summary>
    public string MtlsKeyId { get; set; } = string.Empty;

    /// <summary>HSM label of the client certificate presented for outbound mTLS.</summary>
    public string MtlsCertId { get; set; } = string.Empty;

    /// <summary>HSM label of the certificate chain used to validate the DICT server (PIXCertChainId).</summary>
    public string ServerCertChainId { get; set; } = string.Empty;

    /// <summary>Whether to request gzip-compressed responses from the DICT API.</summary>
    public bool UseGzip { get; set; }

    /// <summary>Whether to verify the DICT server's TLS host name.</summary>
    public bool VerifyHostName { get; set; } = true;
}
