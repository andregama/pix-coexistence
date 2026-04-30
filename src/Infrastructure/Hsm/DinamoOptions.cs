namespace ConvivenciaPix.Infrastructure.Hsm;

public sealed class DinamoOptions
{
    /// <summary>
    /// HSM hostname or IP. In LocalDinamoSdkClient (non-production), treated as a PFX file path.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Default Dinamo HSM management port.</summary>
    public int Port { get; set; } = 4433;

    public string UserId { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>Label of the certificate stored in the HSM slot.</summary>
    public string CertificateLabel { get; set; } = string.Empty;

    /// <summary>Label of the private key stored in the HSM slot.</summary>
    public string KeyLabel { get; set; } = string.Empty;

    /// <summary>
    /// Signing mechanism. Matches DinamoAPI mechanism constants.
    /// Default: RSA_PKCS1_V1_5 (equivalent to DNET.ALG_RSA_2048 with MODE_PKCS1_V1_5).
    /// </summary>
    public string SignMechanism { get; set; } = "RSA_PKCS1_V1_5";
}
