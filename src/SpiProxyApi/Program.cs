using ConvivenciaPix.Application;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.PullStream;
using ConvivenciaPix.Infrastructure;
using ConvivenciaPix.Infrastructure.Metrics;
using ConvivenciaPix.Infrastructure.Outbound;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Reflection;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Swagger / OpenAPI documentation
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Pix Coexistence SPI Proxy API", Version = "v1" });
    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    c.IncludeXmlComments(xmlPath);
});

// Rate Limiting: Fixed Window strategy (100 req / 10s)
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.Identity?.Name ?? httpContext.Request.Headers.Host.ToString(),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 100,
                QueueLimit = 10,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                Window = TimeSpan.FromSeconds(10)
            }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

builder.Services.Configure<OutboundStreamOptions>(builder.Configuration.GetSection("ProxyApi:Outbound"));
builder.Services.Configure<PullStreamOptions>(builder.Configuration.GetSection("ProxyApi:Outbound"));
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

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Pix Coexistence SPI Proxy API V1");
    c.RoutePrefix = "swagger";
});

app.UseRateLimiter();

// Serve the local coexistence-analytics dashboard (wwwroot/analytics.html) same-origin,
// so it can call /api/v1/analytics/* with relative URLs and no CORS configuration.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/healthz");
app.MapHealthChecks("/healthz/ready", new HealthCheckOptions { Predicate = _ => true });
app.MapPrometheusScrapingEndpoint("/metrics");

app.Run();

public partial class Program { }
