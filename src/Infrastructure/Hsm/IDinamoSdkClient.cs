namespace ConvivenciaPix.Infrastructure.Hsm;

/// <summary>
/// Mirrors the DinamoAPI.NET (Dinamo.Hsm) contract for the PIX/SPI signing operations used by
/// this solution. Implementations:
///   - LocalDinamoSdkClient: .NET BCL only — no native HSM library required. Used in Staging.
///   - DinamoNetSdkClient:   wraps the real Dinamo.Hsm.DinamoClient. Used in Production.
///
/// The HSM signs the whole envelope internally (SignPIX) and validates it (VerifyPIX); keys and
/// certificates are referenced by label and never leave the HSM.
/// </summary>
public interface IDinamoSdkClient
{
    /// <summary>Opens an authenticated session to the HSM.</summary>
    void Connect(string host, int port, string userId, string password);

    /// <summary>
    /// Signs the unsigned PIX/SPI envelope with the private key <paramref name="keyId"/> and
    /// certificate <paramref name="certId"/>, returning the signed envelope bytes.
    /// Equivalent to DinamoClient.SignPIX(keyId, certId, unsignedEnvelope).
    /// </summary>
    byte[] SignPIX(string keyId, string certId, byte[] unsignedEnvelope);

    /// <summary>
    /// Verifies the signature on <paramref name="signedEnvelope"/> against the certificate chain
    /// <paramref name="chainId"/> and revocation list <paramref name="crl"/>.
    /// Equivalent to DinamoClient.VerifyPIX(chainId, crl, signedEnvelope).
    /// </summary>
    bool VerifyPIX(string chainId, string crl, string signedEnvelope);

    /// <summary>
    /// Signs an unsigned DICT message with the private key <paramref name="keyId"/> and certificate
    /// <paramref name="certId"/>, returning the signed message bytes. DICT uses a different signature
    /// profile from SPI (root-enveloped, no BAH/Sgntr). Equivalent to
    /// DinamoClient.SignPIXDict(keyId, certId, unsignedMessage).
    /// </summary>
    byte[] SignPIXDict(string keyId, string certId, byte[] unsignedMessage);

    /// <summary>
    /// Verifies the signature on a signed DICT message against the certificate chain
    /// <paramref name="chainId"/> and revocation list <paramref name="crl"/>.
    /// Equivalent to DinamoClient.VerifyPIXDict(chainId, crl, signedMessage).
    /// </summary>
    bool VerifyPIXDict(string chainId, string crl, byte[] signedMessage);

    /// <summary>Closes the HSM session and releases the connection.</summary>
    void Disconnect();
}
