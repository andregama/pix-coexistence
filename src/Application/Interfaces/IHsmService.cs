namespace ConvivenciaPix.Application.Interfaces;

/// <summary>
/// Abstraction over HSM hardware (Dinamo in production, self-signed PFX in development).
/// All XML signing operations must go through this interface — never access the HSM SDK directly.
/// The HSM signs the entire PIX/SPI envelope internally (Dinamo SignPIX), placing the signature
/// in AppHdr/Sgntr, so callers hand over unsigned XML and receive signed XML.
/// </summary>
public interface IHsmService
{
    /// <summary>Signs the PIX/SPI envelope and returns the signed XML.</summary>
    Task<string> SignXmlAsync(string unsignedXml, CancellationToken cancellationToken = default);

    /// <summary>Verifies the signature on a signed PIX/SPI envelope.</summary>
    Task<bool> VerifyXmlAsync(string signedXml, CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs a DICT message and returns the signed XML. DICT bodies have no BAH/AppHdr, so the
    /// signature is enveloped at the document root — the HSM uses its dedicated DICT operation
    /// (Dinamo SignPIXDict), distinct from the SPI envelope signing above.
    /// </summary>
    Task<string> SignDictXmlAsync(string unsignedXml, CancellationToken cancellationToken = default);

    /// <summary>Verifies the signature on a signed DICT message.</summary>
    Task<bool> VerifyDictXmlAsync(string signedXml, CancellationToken cancellationToken = default);
}
