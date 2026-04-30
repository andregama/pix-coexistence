using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Infrastructure.Hsm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography.X509Certificates;

namespace ConvivenciaPix.Infrastructure.Signing;

/// <summary>
/// Production IHsmService backed by the Dinamo HSM via IDinamoSdkClient.
/// Connects and disconnects per operation — HSM sessions are not shared across threads.
/// The signing certificate is cached after the first successful retrieval.
/// </summary>
public sealed class DinamoHsmService : IHsmService
{
    private readonly IDinamoSdkClient _sdk;
    private readonly DinamoOptions _options;
    private readonly ILogger<DinamoHsmService> _logger;
    private X509Certificate2? _cachedCertificate;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public DinamoHsmService(
        IDinamoSdkClient sdk,
        IOptions<DinamoOptions> options,
        ILogger<DinamoHsmService> logger)
    {
        _sdk = sdk;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<X509Certificate2> GetSigningCertificateAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedCertificate is not null)
            return _cachedCertificate;

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedCertificate is not null)
                return _cachedCertificate;

            _logger.LogHsmConnect(_options.Host, _options.Port);
            _sdk.Connect(_options.Host, _options.Port, _options.UserId, _options.Password);
            try
            {
                _cachedCertificate = _sdk.GetCertificate(_options.CertificateLabel);
                _logger.LogHsmCertLoaded(_options.CertificateLabel, _cachedCertificate.Thumbprint);
            }
            finally
            {
                _sdk.Disconnect();
            }

            return _cachedCertificate;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task<byte[]> SignDataAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        _logger.LogHsmConnect(_options.Host, _options.Port);
        _sdk.Connect(_options.Host, _options.Port, _options.UserId, _options.Password);
        try
        {
            var signature = _sdk.Sign(_options.KeyLabel, data, _options.SignMechanism);
            _logger.LogHsmSigned(_options.KeyLabel, data.Length, signature.Length);
            return await Task.FromResult(signature);
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

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Signing certificate loaded from HSM. Label={CertificateLabel} Thumbprint={Thumbprint}")]
    public static partial void LogHsmCertLoaded(this ILogger logger, string certificateLabel, string thumbprint);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Data signed by HSM. KeyLabel={KeyLabel} InputBytes={InputBytes} SignatureBytes={SignatureBytes}")]
    public static partial void LogHsmSigned(this ILogger logger, string keyLabel, int inputBytes, int signatureBytes);
}
