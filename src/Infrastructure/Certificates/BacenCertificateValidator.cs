using ConvivenciaPix.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace ConvivenciaPix.Infrastructure.Certificates;

/// <summary>
/// Production ICertificateValidator. Enforces:
///   1. No TLS chain/policy errors (when ValidateChain is true)
///   2. Certificate not expired
///   3. Thumbprint in the trusted list (when TrustedThumbprints is non-empty)
/// </summary>
public sealed class BacenCertificateValidator : ICertificateValidator
{
    private readonly CertificateValidatorOptions _options;
    private readonly ILogger<BacenCertificateValidator> _logger;

    public BacenCertificateValidator(
        IOptions<CertificateValidatorOptions> options,
        ILogger<BacenCertificateValidator> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsValid(X509Certificate2 certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        var thumbprint = certificate.Thumbprint;
        var subject = certificate.Subject;
        var expiry = certificate.NotAfter;

        if (_options.ValidateChain && sslPolicyErrors != SslPolicyErrors.None)
        {
            _logger.LogCertRejectedPolicyErrors(thumbprint, subject, sslPolicyErrors);
            return false;
        }

        if (DateTime.UtcNow > expiry.ToUniversalTime())
        {
            _logger.LogCertRejectedExpired(thumbprint, subject, expiry);
            return false;
        }

        if (_options.TrustedThumbprints.Length > 0)
        {
            var normalised = thumbprint.Replace(":", "").ToUpperInvariant();
            var trusted = _options.TrustedThumbprints
                .Select(t => t.Replace(":", "").ToUpperInvariant())
                .ToHashSet();

            if (!trusted.Contains(normalised))
            {
                _logger.LogCertRejectedUntrusted(thumbprint, subject);
                return false;
            }
        }

        _logger.LogCertAccepted(thumbprint, subject);
        return true;
    }
}

internal static partial class BacenCertificateValidatorLogMessages
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Client certificate rejected — TLS policy errors. Thumbprint={Thumbprint} Subject={Subject} Errors={SslPolicyErrors}")]
    public static partial void LogCertRejectedPolicyErrors(this ILogger logger, string thumbprint, string subject, SslPolicyErrors sslPolicyErrors);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Client certificate rejected — expired. Thumbprint={Thumbprint} Subject={Subject} Expiry={Expiry:O}")]
    public static partial void LogCertRejectedExpired(this ILogger logger, string thumbprint, string subject, DateTime expiry);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Client certificate rejected — thumbprint not in trusted list. Thumbprint={Thumbprint} Subject={Subject}")]
    public static partial void LogCertRejectedUntrusted(this ILogger logger, string thumbprint, string subject);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Client certificate accepted. Thumbprint={Thumbprint} Subject={Subject}")]
    public static partial void LogCertAccepted(this ILogger logger, string thumbprint, string subject);
}
