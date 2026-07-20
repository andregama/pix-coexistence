using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Infrastructure.Hsm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace ConvivenciaPix.Infrastructure.Signing;

/// <summary>
/// Production IHsmService backed by the Dinamo HSM via IDinamoSdkClient. The HSM signs and
/// verifies the whole PIX/SPI envelope (SignPIX/VerifyPIX); this service just brackets those calls
/// with a connect/disconnect, since HSM sessions are not shared across threads.
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

        _logger.LogHsmConnect(_options.Host, _options.Port);
        _sdk.Connect(_options.Host, _options.Port, _options.UserId, _options.Password);
        try
        {
            var signed = _sdk.SignPIX(_options.KeyId, _options.CertId, Encoding.UTF8.GetBytes(toSign));
            _logger.LogHsmSigned(_options.KeyId, toSign.Length, signed.Length);
            return Task.FromResult(Encoding.UTF8.GetString(signed));
        }
        finally
        {
            _sdk.Disconnect();
        }
    }

    public Task<bool> VerifyXmlAsync(string signedXml, CancellationToken cancellationToken = default)
    {
        _logger.LogHsmConnect(_options.Host, _options.Port);
        _sdk.Connect(_options.Host, _options.Port, _options.UserId, _options.Password);
        try
        {
            return Task.FromResult(_sdk.VerifyPIX(_options.ChainId, _options.Crl, signedXml));
        }
        finally
        {
            _sdk.Disconnect();
        }
    }
}

internal static partial class DinamoHsmServiceLogMessages
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Connecting to Dinamo HSM at {Host}:{Port}")]
    public static partial void LogHsmConnect(this ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "PIX envelope signed by HSM. KeyId={KeyId} InputBytes={InputBytes} SignedBytes={SignedBytes}")]
    public static partial void LogHsmSigned(this ILogger logger, string keyId, int inputBytes, int signedBytes);
}
