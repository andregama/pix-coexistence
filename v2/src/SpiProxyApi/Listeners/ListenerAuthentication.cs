using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;

namespace ConvivenciaPix.V2.SpiProxyApi.Listeners;

/// <summary>
/// mTLS for the three listeners: a client certificate is accepted only when its thumbprint is on
/// the allow-list of the listener it was presented on, so System A's certificate cannot be used
/// on System B's port (and neither can reach the admin port).
/// </summary>
public static class ListenerAuthentication
{
    public const string DevelopmentScheme = "Development";

    public static IServiceCollection AddListenerAuthentication(this IServiceCollection services, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            // Local development runs over plain HTTP against the simulator: there is no client
            // certificate to check. Never registered outside Development.
            services.AddAuthentication(DevelopmentScheme)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(DevelopmentScheme, _ => { });
            return services;
        }

        services
            .AddAuthentication(CertificateAuthenticationDefaults.AuthenticationScheme)
            .AddCertificate(options =>
            {
                options.AllowedCertificateTypes = CertificateTypes.All;
                options.Events = new CertificateAuthenticationEvents
                {
                    OnCertificateValidated = context =>
                    {
                        var listeners = context.HttpContext.RequestServices.GetRequiredService<IOptions<ListenerOptions>>().Value;
                        var kind = context.HttpContext.GetListener();

                        if (IsTrusted(context.ClientCertificate, listeners.TrustedThumbprints(kind)))
                        {
                            context.Success();
                        }
                        else
                        {
                            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                                .CreateLogger(typeof(ListenerAuthentication))
                                .LogCertificateRejected(context.ClientCertificate.Thumbprint, context.ClientCertificate.Subject, kind);
                            context.Fail("Client certificate is not trusted on this listener.");
                        }

                        return Task.CompletedTask;
                    }
                };
            });

        return services;
    }

    /// <summary>An empty allow-list trusts nobody: a listener must name its callers explicitly.</summary>
    public static bool IsTrusted(X509Certificate2 certificate, string[] trustedThumbprints)
    {
        var now = DateTime.UtcNow;
        if (now > certificate.NotAfter.ToUniversalTime() || now < certificate.NotBefore.ToUniversalTime())
            return false;

        var thumbprint = Normalize(certificate.Thumbprint);
        return trustedThumbprints.Any(trusted => Normalize(trusted) == thumbprint);
    }

    private static string Normalize(string thumbprint) =>
        thumbprint.Replace(":", "").Replace(" ", "").ToUpperInvariant();
}

internal sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, Context.GetListener().ToString())], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

internal static partial class ListenerAuthenticationLogMessages
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Client certificate rejected on listener {Listener}. Thumbprint={Thumbprint} Subject={Subject}")]
    public static partial void LogCertificateRejected(this ILogger logger, string thumbprint, string subject, ListenerKind listener);
}
