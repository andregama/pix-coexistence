namespace ConvivenciaPix.Infrastructure.Certificates;

public sealed class CertificateValidatorOptions
{
    /// <summary>
    /// SHA-1 thumbprints of trusted client certificates (Bacen-issued).
    /// When empty, thumbprint validation is skipped (not recommended for production).
    /// Set via env: CertificateValidator__TrustedThumbprints__0, __1, ...
    /// </summary>
    public string[] TrustedThumbprints { get; set; } = [];

    /// <summary>
    /// When true, rejects certificates with any SslPolicyErrors (chain errors, name mismatch, etc.).
    /// Should always be true in production.
    /// </summary>
    public bool ValidateChain { get; set; } = true;
}
