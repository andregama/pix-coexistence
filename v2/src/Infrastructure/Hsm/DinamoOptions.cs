namespace ConvivenciaPix.V2.Infrastructure.Hsm;

public sealed class DinamoOptions
{
    /// <summary>
    /// HSM hostname or IP. In LocalDinamoSdkClient (non-production), treated as a PFX file path.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>Id (label) of the signing certificate stored in the HSM. Passed to SignPIX.</summary>
    public string CertId { get; set; } = string.Empty;

    /// <summary>Id (label) of the signing private key stored in the HSM. Passed to SignPIX.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>Id (label) of the certificate chain VerifyPIX checks Bacen's signature against.</summary>
    public string ChainId { get; set; } = string.Empty;

    /// <summary>Certificate revocation list reference used by VerifyPIX (may be empty).</summary>
    public string Crl { get; set; } = string.Empty;
}
