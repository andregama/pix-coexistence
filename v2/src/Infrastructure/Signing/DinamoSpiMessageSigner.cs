using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Infrastructure.Hsm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace ConvivenciaPix.V2.Infrastructure.Signing;

/// <summary>
/// <see cref="ISpiMessageSigner"/> backed by the Dinamo HSM via <see cref="IDinamoSdkClient"/>. The
/// HSM signs (SignPIX) and verifies (VerifyPIX) the whole SPI envelope internally. Session lifecycle
/// and thread safety are owned by the SDK client.
/// </summary>
public sealed class DinamoSpiMessageSigner : ISpiMessageSigner
{
    private readonly IDinamoSdkClient _sdk;
    private readonly DinamoOptions _options;
    private readonly ILogger<DinamoSpiMessageSigner> _logger;

    public DinamoSpiMessageSigner(
        IDinamoSdkClient sdk, IOptions<DinamoOptions> options, ILogger<DinamoSpiMessageSigner> logger)
    {
        _sdk = sdk;
        _options = options.Value;
        _logger = logger;
    }

    public byte[] Sign(byte[] xml)
    {
        // SignPIX envelops internally, so Bacen's signature has to go first — otherwise the system
        // would receive an envelope with two signatures.
        var toSign = EnvelopedXmlSigner.StripSignatures(xml);
        var signed = _sdk.SignPIX(_options.KeyId, _options.CertId, toSign);
        _logger.LogHsmSigned(_options.KeyId, toSign.Length, signed.Length);
        return signed;
    }

    public bool Verify(byte[] xml) =>
        // The SDK expects a null CRL reference (not an empty string) when no revocation list is used.
        _sdk.VerifyPIX(_options.ChainId, string.IsNullOrEmpty(_options.Crl) ? null : _options.Crl, Encoding.UTF8.GetString(xml));
}

internal static partial class DinamoSpiMessageSignerLogMessages
{
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "SPI envelope signed by HSM. KeyId={KeyId} InputBytes={InputBytes} SignedBytes={SignedBytes}")]
    public static partial void LogHsmSigned(this ILogger logger, string keyId, int inputBytes, int signedBytes);
}
