using ConvivenciaPix.Application;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.ReceiveSpiRequest;
using ConvivenciaPix.Infrastructure;
using ConvivenciaPix.Infrastructure.Metrics;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.Configure<SpiProxyOptions>(builder.Configuration.GetSection("ProxyApi"));
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

// mTLS: require client certificate at the TLS layer in non-Development environments.
// In Production, Kestrel:Endpoints:Https:ClientCertificateMode = RequireCertificate
// is set via appsettings.Production.json / environment variables.
if (!builder.Environment.IsDevelopment())
{
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.ConfigureHttpsDefaults(https =>
        {
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
        });
    });
}

// Certificate authentication middleware validates the client cert via ICertificateValidator.
// In Development, DevCertificateValidator always returns true (no cert required).
// In Production, BacenCertificateValidator enforces thumbprint + expiry + chain errors.
builder.Services
    .AddAuthentication(CertificateAuthenticationDefaults.AuthenticationScheme)
    .AddCertificate(options =>
    {
        options.AllowedCertificateTypes = CertificateTypes.All;
        options.Events = new CertificateAuthenticationEvents
        {
            OnCertificateValidated = context =>
            {
                var validator = context.HttpContext.RequestServices
                    .GetRequiredService<ICertificateValidator>();

                if (validator.IsValid(
                    context.ClientCertificate,
                    chain: null,
                    System.Net.Security.SslPolicyErrors.None))
                {
                    context.Success();
                }
                else
                {
                    context.Fail("Client certificate validation failed.");
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddHealthChecks()
    .AddSqlServer(
        builder.Configuration.GetConnectionString("SqlServer")!,
        name: "sqlserver",
        tags: ["db"])
    .AddRedis(
        builder.Configuration.GetConnectionString("Redis")!,
        name: "redis",
        tags: ["cache"]);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("spi-proxy-api"))
    .WithTracing(t => t
        .AddSource("ConvivenciaPix")
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter(o =>
            o.Endpoint = new Uri(builder.Configuration["Otel:Endpoint"] ?? "http://localhost:4317")))
    .WithMetrics(m => m
        .AddMeter(SpiMetrics.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddPrometheusExporter());

var app = builder.Build();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/healthz");
app.MapHealthChecks("/healthz/ready", new HealthCheckOptions { Predicate = _ => true });
app.MapPrometheusScrapingEndpoint("/metrics");

app.Run();

public partial class Program { }
