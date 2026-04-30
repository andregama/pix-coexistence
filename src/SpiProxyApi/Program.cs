using ConvivenciaPix.Infrastructure;
using ConvivenciaPix.SpiProxyApi.Options;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.Configure<ProxyApiOptions>(builder.Configuration.GetSection("ProxyApi"));
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

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
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter(o =>
            o.Endpoint = new Uri(builder.Configuration["Otel:Endpoint"] ?? "http://localhost:4317")));

var app = builder.Build();

// mTLS enforcement: configure Kestrel client certificate in production (Phase 3)
app.UseHttpsRedirection();
app.MapControllers();
app.MapHealthChecks("/healthz");
app.MapHealthChecks("/healthz/ready", new HealthCheckOptions { Predicate = _ => true });

app.Run();
