using ConvivenciaPix.Application.Interfaces;
using Microsoft.Extensions.Logging;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace ConvivenciaPix.Infrastructure.Certificates;

/// <summary>
/// Development-only ICertificateValidator — bypasses all mTLS validation.
/// Registered only when IHostEnvironment.IsDevelopment() is true.
/// </summary>
public sealed class DevCertificateValidator : ICertificateValidator
{
    private readonly ILogger<DevCertificateValidator> _logger;

    public DevCertificateValidator(ILogger<DevCertificateValidator> logger)
        => _logger = logger;

    public bool IsValid(X509Certificate2 certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        _logger.LogDevBypass(certificate.Thumbprint, certificate.Subject);
        return true;
    }
}

internal static partial class DevCertificateValidatorLogMessages
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "DevCertificateValidator bypassing mTLS validation — DO NOT use in production. " +
                  "Thumbprint={Thumbprint} Subject={Subject}")]
    public static partial void LogDevBypass(this ILogger logger, string thumbprint, string subject);
}
