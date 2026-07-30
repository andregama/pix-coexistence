using ConvivenciaPix.Infrastructure.Outbound;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography.X509Certificates;

namespace ConvivenciaPix.Infrastructure.Certificates;

/// <summary>
/// Production <see cref="IDictClientCertificateProvider"/>. Loads the outbound mTLS client
/// certificate from the local machine store by thumbprint. The certificate's private key is an HSM-
/// backed CNG key (Dinamo CNG Key Storage Provider), so signing during the TLS handshake happens in
/// the HSM and the key never leaves it. Requires the Dinamo CNG provider to be installed and the
/// certificate imported/enabled for the local machine.
/// </summary>
public sealed class HsmBackedClientCertificateProvider : IDictClientCertificateProvider
{
    private readonly DictProxyOptions _options;
    private readonly ILogger<HsmBackedClientCertificateProvider> _logger;
    private X509Certificate2? _cached;

    public HsmBackedClientCertificateProvider(
        IOptions<DictProxyOptions> options,
        ILogger<HsmBackedClientCertificateProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public X509Certificate2 GetClientCertificate()
    {
        if (_cached is not null)
            return _cached;

        if (string.IsNullOrWhiteSpace(_options.ClientCertThumbprint))
            throw new InvalidOperationException(
                "DictProxy:ClientCertThumbprint is required for HSM-backed outbound mTLS.");

        var thumbprint = _options.ClientCertThumbprint.Replace(" ", "").Replace(":", "").ToUpperInvariant();

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);

        if (matches.Count == 0)
            throw new InvalidOperationException(
                $"DICT client certificate with thumbprint '{thumbprint}' not found in LocalMachine\\My. " +
                "Ensure the HSM-backed certificate is imported and the Dinamo CNG provider is installed.");

        _cached = matches[0];
        _logger.LogDictClientCertResolved(thumbprint, _cached.Subject, _cached.HasPrivateKey);
        return _cached;
    }
}

internal static partial class HsmBackedClientCertificateProviderLogMessages
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "DICT client certificate resolved from machine store. Thumbprint={Thumbprint} Subject={Subject} HasPrivateKey={HasPrivateKey}")]
    public static partial void LogDictClientCertResolved(this ILogger logger, string thumbprint, string subject, bool hasPrivateKey);
}
