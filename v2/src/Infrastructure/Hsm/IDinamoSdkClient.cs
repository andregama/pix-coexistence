namespace ConvivenciaPix.V2.Infrastructure.Hsm;

/// <summary>
/// The slice of the DinamoAPI.NET (Dinamo.Hsm) contract this proxy uses: signing and verifying SPI
/// envelopes, and the mTLS HTTP request to the SPI performed by the HSM. Implementations:
///   - LocalDinamoSdkClient: .NET BCL only — no native HSM library required. Used in Staging.
///   - DinamoNetSdkClient:   wraps the real Dinamo.Hsm.DinamoClient. Used in Production.
///
/// Implementations own their HSM session lifecycle (Dinamo sessions have thread-session affinity
/// and must not be shared across threads simultaneously), so callers never manage Connect/Disconnect.
/// </summary>
public interface IDinamoSdkClient
{
    /// <summary>
    /// Signs the unsigned SPI envelope with the private key <paramref name="keyId"/> and
    /// certificate <paramref name="certId"/>, returning the signed envelope bytes.
    /// Equivalent to DinamoClient.SignPIX(keyId, certId, unsignedEnvelope).
    /// </summary>
    byte[] SignPIX(string keyId, string certId, byte[] unsignedEnvelope);

    /// <summary>
    /// Verifies the signature on <paramref name="signedEnvelope"/> against the certificate chain
    /// <paramref name="chainId"/> and revocation list <paramref name="crl"/> (null when none).
    /// Equivalent to DinamoClient.VerifyPIX(chainId, crl, signedEnvelope).
    /// </summary>
    bool VerifyPIX(string chainId, string? crl, string signedEnvelope);

    /// <summary>
    /// Performs an mTLS HTTP request to the SPI through the HSM. The HSM owns the TLS handshake and
    /// presents the client certificate <paramref name="certId"/> / key <paramref name="keyId"/>
    /// (referenced by label, never leaving the HSM), validating the server against
    /// <paramref name="serverCertChainId"/>. Equivalent to DinamoClient.postPIX/putPIX/getPIX/deletePIX
    /// followed by getPIXHTTPReqCode() on the same session.
    /// <paramref name="requestHeaders"/> entries are formatted "Name: Value" (no CRLF).
    /// This call blocks for the duration of the request.
    /// </summary>
    PixHttpResponse SendPix(
        PixHttpMethod method,
        string keyId,
        string certId,
        string serverCertChainId,
        string url,
        IReadOnlyList<string> requestHeaders,
        byte[] body,
        int timeoutSeconds,
        bool useGzip,
        bool verifyHostName);
}
