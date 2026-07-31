using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Infrastructure.Hsm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace ConvivenciaPix.Infrastructure.Signing;

/// <summary>
/// Production IHsmService backed by the Dinamo HSM via IDinamoSdkClient. The HSM signs and verifies
/// the whole PIX/SPI (SignPIX) or DICT (SignPIXDict) message internally. Session lifecycle and thread
/// safety are owned by the SDK client, so this service just brackets the strip/encode around a single
/// self-contained call.
/// </summary>
public sealed class DinamoHsmService : IHsmService
{
    private readonly IDinamoSdkClient _sdk;
    private readonly DinamoOptions _options;
    private readonly ILogger<DinamoHsmService> _logger;

    public DinamoHsmService(
        IDinamoSdkClient sdk,
        IOptions<DinamoOptions> options,
        ILogger<DinamoHsmService> logger)
    {
        _sdk = sdk;
        _options = options.Value;
        _logger = logger;
    }

    public Task<string> SignXmlAsync(string unsignedXml, CancellationToken cancellationToken = default)
    {
        // Bacen responses arrive already signed; the HSM's SignPIX envelops internally, so strip any
        // existing <Signature> here to avoid delivering a double-signed envelope.
        var toSign = EnvelopedXmlSigner.StripSignatures(unsignedXml);
        var signed = _sdk.SignPIX(_options.KeyId, _options.CertId, Encoding.UTF8.GetBytes(toSign));
        _logger.LogHsmSigned(_options.KeyId, toSign.Length, signed.Length);
        return Task.FromResult(Encoding.UTF8.GetString(signed));
    }

    public Task<bool> VerifyXmlAsync(string signedXml, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sdk.VerifyPIX(_options.ChainId, NullIfEmpty(_options.Crl), signedXml));

    public Task<string> SignDictXmlAsync(string unsignedXml, CancellationToken cancellationToken = default)
    {
        // DICT messages arrive already signed (by System B, or by Bacen on the response); the HSM's
        // SignPIXDict envelops internally, so strip any existing <Signature> to avoid a double sign.
        var toSign = EnvelopedXmlSigner.StripSignatures(unsignedXml);
        var signed = _sdk.SignPIXDict(_options.KeyId, _options.CertId, Encoding.UTF8.GetBytes(toSign));
        _logger.LogHsmSigned(_options.KeyId, toSign.Length, signed.Length);
        return Task.FromResult(Encoding.UTF8.GetString(signed));
    }

    public Task<bool> VerifyDictXmlAsync(string signedXml, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sdk.VerifyPIXDict(_options.ChainId, NullIfEmpty(_options.Crl), Encoding.UTF8.GetBytes(signedXml)));

    // The SDK expects a null CRL reference (not an empty string) when no revocation list is used.
    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
}

internal static partial class DinamoHsmServiceLogMessages
{
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "PIX envelope signed by HSM. KeyId={KeyId} InputBytes={InputBytes} SignedBytes={SignedBytes}")]
    public static partial void LogHsmSigned(this ILogger logger, string keyId, int inputBytes, int signedBytes);
}
