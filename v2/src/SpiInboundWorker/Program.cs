using ConvivenciaPix.V2.Application;
using ConvivenciaPix.V2.Infrastructure;
using ConvivenciaPix.V2.Infrastructure.Metrics;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

// The single consumer of the bank's SPI output stream: pulls from Bacen, routes each message to
// System A and/or System B and stores it in their delivery queues. Also publishes the audit outbox
// and purges expired rows.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddApplication(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddInboundWorker(builder.Configuration);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("spi-inbound-worker-v2"))
    .WithMetrics(m => m
        .AddMeter(ProxyMetrics.MeterName)
        .AddOtlpExporter(o =>
            o.Endpoint = new Uri(builder.Configuration["Otel:Endpoint"] ?? "http://localhost:4317")));

var host = builder.Build();
host.Run();
