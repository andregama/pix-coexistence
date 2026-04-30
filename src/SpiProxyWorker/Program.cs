using ConvivenciaPix.Application;
using ConvivenciaPix.Infrastructure;
using ConvivenciaPix.Infrastructure.Metrics;
using ConvivenciaPix.SpiProxyWorker.Consumers;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddHostedService<SystemAResponseProxyConsumer>();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("spi-proxy-worker"))
    .WithTracing(t => t
        .AddSource("ConvivenciaPix")
        .AddOtlpExporter(o =>
            o.Endpoint = new Uri(builder.Configuration["Otel:Endpoint"] ?? "http://localhost:4317")))
    .WithMetrics(m => m
        .AddMeter(SpiMetrics.MeterName)
        .AddOtlpExporter(o =>
            o.Endpoint = new Uri(builder.Configuration["Otel:Endpoint"] ?? "http://localhost:4317")));

var host = builder.Build();
host.Run();
