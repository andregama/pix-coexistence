namespace ConvivenciaPix.V2.Application.Interfaces;

/// <summary>
/// The bank's signing identity for SPI messages (Dinamo HSM in production, a self-signed PFX in
/// development). Every message the proxy delivers to a system is signed through this interface;
/// keys never leave the HSM. Calls block for the duration of the HSM operation.
/// </summary>
public interface ISpiMessageSigner
{
    /// <summary>
    /// Signs the SPI envelope, placing the signature in <c>AppHdr/Sgntr</c>. Any signature already
    /// present (Bacen's) is replaced, never kept alongside the new one.
    /// </summary>
    byte[] Sign(byte[] xml);

    /// <summary>Verifies the signature on an SPI envelope received from Bacen.</summary>
    bool Verify(byte[] xml);
}

public sealed class InboundSigningOptions
{
    /// <summary>
    /// Verify Bacen's signature before a message is split or re-signed. Once the proxy re-signs, the
    /// receiving system can no longer check Bacen's signature, so this is the only place it happens.
    /// Turn off only where the counterpart does not sign (the local simulator).
    /// </summary>
    public bool VerifyBacenSignature { get; set; } = true;
}
