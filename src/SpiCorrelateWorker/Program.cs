using ConvivenciaPix.Application;
using ConvivenciaPix.Infrastructure;
using ConvivenciaPix.Infrastructure.Metrics;
using ConvivenciaPix.Application.UseCases.CorrelateMessages;
using ConvivenciaPix.SpiCorrelateWorker;
using ConvivenciaPix.SpiCorrelateWorker.Consumers;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<CorrelationOptions>(builder.Configuration.GetSection("Correlation"));
builder.Services.Configure<CorrelateInboundRetryOptions>(
    builder.Configuration.GetSection("Correlation:Retry"));
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddHostedService<SystemACdcCorrelateConsumer>();
builder.Services.AddHostedService<SystemBOutboundCorrelateConsumer>();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("spi-correlate-worker"))
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
