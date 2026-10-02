using ConvivenciaPix.V2.Application;
using ConvivenciaPix.V2.Infrastructure;
using ConvivenciaPix.V2.Infrastructure.Metrics;
using ConvivenciaPix.V2.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

// The single consumer of the bank's SPI output stream: pulls from Bacen, routes each message to
// System A and/or System B and stores it in their delivery queues. Also publishes the audit outbox
// and purges expired rows.
var builder = Host.CreateApplicationBuilder(args);
if (StorageOptions.IsInMemory(builder.Configuration))
    throw new InvalidOperationException(
        "Storage:Provider=InMemory keeps everything inside the API process, which then runs the worker's services itself. Run only SpiProxyApi.");
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
