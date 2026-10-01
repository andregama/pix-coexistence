using ConvivenciaPix.V2.Application;
using ConvivenciaPix.V2.Infrastructure;
using ConvivenciaPix.V2.Infrastructure.Metrics;
using ConvivenciaPix.V2.SpiProxyApi.Controllers;
using ConvivenciaPix.V2.SpiProxyApi.Listeners;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Pix Coexistence SPI Proxy API (v2)", Version = "v1" });
    c.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml"));
});

builder.Services.Configure<ListenerOptions>(builder.Configuration.GetSection("Listeners"));
builder.Services.Configure<SpiOptions>(builder.Configuration.GetSection("Spi"));
builder.Services.AddSingleton<ICallerSystemResolver, PortCallerSystemResolver>();

builder.Services.AddApplication(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

// A real SPI client may gzip what it sends and expect gzip back.
builder.Services.AddRequestDecompression();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["multipart/mixed"]);
});

// One listener per system (the port identifies the caller) plus one for administration. Outside
// Development every listener is TLS with a required client certificate.
var listeners = builder.Configuration.GetSection("Listeners").Get<ListenerOptions>() ?? new ListenerOptions();
var useHttps = listeners.Https ?? !builder.Environment.IsDevelopment();
builder.WebHost.ConfigureKestrel(kestrel =>
{
    var serverCertificate = useHttps && !string.IsNullOrEmpty(listeners.Certificate.Path)
        ? new X509Certificate2(listeners.Certificate.Path, listeners.Certificate.Password)
        : null;

    foreach (var port in new[] { listeners.SystemAPort, listeners.SystemBPort, listeners.AdminPort })
    {
        kestrel.ListenAnyIP(port, listen =>
        {
            if (!useHttps) return;
            listen.UseHttps(https =>
            {
                if (serverCertificate is not null)
                    https.ServerCertificate = serverCertificate;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            });
        });
    }
});

builder.Services.AddListenerAuthentication(builder.Environment);
builder.Services.AddAuthorization();

var healthChecks = builder.Services.AddHealthChecks()
    .AddSqlServer(builder.Configuration.GetConnectionString("SqlServer")!, name: "sqlserver", tags: ["db"]);
// Redis only carries wake-up notifications: losing it degrades latency, not correctness.
if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Redis")))
    healthChecks.AddRedis(
        builder.Configuration.GetConnectionString("Redis")!, name: "redis", failureStatus: HealthStatus.Degraded, tags: ["notify"]);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("spi-proxy-api-v2"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter(o =>
            o.Endpoint = new Uri(builder.Configuration["Otel:Endpoint"] ?? "http://localhost:4317")))
    .WithMetrics(m => m
        .AddMeter(ProxyMetrics.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddPrometheusExporter());

var app = builder.Build();

// First in the pipeline: decides which endpoints exist on the listener the request arrived on.
app.UseMiddleware<ListenerGateMiddleware>();

app.UseRequestDecompression();
app.UseResponseCompression();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Pix Coexistence SPI Proxy API (v2)");
    c.RoutePrefix = "swagger";
});

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/healthz/ready");
app.MapPrometheusScrapingEndpoint("/metrics");

app.Run();

public partial class Program { }
