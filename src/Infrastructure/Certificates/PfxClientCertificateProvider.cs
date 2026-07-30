using ConvivenciaPix.Infrastructure.Outbound;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography.X509Certificates;

namespace ConvivenciaPix.Infrastructure.Certificates;

/// <summary>
/// Dev/Staging <see cref="IDictClientCertificateProvider"/> that loads the outbound mTLS client
/// certificate from a local PFX file, mirroring how <see cref="Hsm.LocalDinamoSdkClient"/> loads
/// its signing PFX. Not used in Production, where the key is HSM-backed
/// (<see cref="HsmBackedClientCertificateProvider"/>).
/// </summary>
public sealed class PfxClientCertificateProvider : IDictClientCertificateProvider
{
    private readonly DictProxyOptions _options;
    private readonly ILogger<PfxClientCertificateProvider> _logger;
    private X509Certificate2? _cached;

    public PfxClientCertificateProvider(
        IOptions<DictProxyOptions> options,
        ILogger<PfxClientCertificateProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public X509Certificate2 GetClientCertificate()
    {
        if (_cached is not null)
            return _cached;

        if (string.IsNullOrWhiteSpace(_options.ClientCertPfxPath))
            throw new InvalidOperationException(
                "DictProxy:ClientCertPfxPath is required for outbound mTLS in this environment.");
        if (!File.Exists(_options.ClientCertPfxPath))
            throw new FileNotFoundException(
                $"DICT client certificate PFX not found at '{_options.ClientCertPfxPath}'.",
                _options.ClientCertPfxPath);

        _cached = new X509Certificate2(
            _options.ClientCertPfxPath,
            _options.ClientCertPfxPassword,
            X509KeyStorageFlags.Exportable);

        if (_cached.GetRSAPrivateKey() is null)
            throw new InvalidOperationException(
                $"DICT client certificate PFX at '{_options.ClientCertPfxPath}' has no RSA private key.");

        _logger.LogDictClientCertLoaded(_options.ClientCertPfxPath, _cached.Thumbprint);
        return _cached;
    }
}

internal static partial class PfxClientCertificateProviderLogMessages
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "DICT client certificate loaded from PFX {PfxPath}. Thumbprint={Thumbprint}. DO NOT use in production.")]
    public static partial void LogDictClientCertLoaded(this ILogger logger, string pfxPath, string thumbprint);
}
