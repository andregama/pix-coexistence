using Confluent.Kafka;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Infrastructure.Messaging.Dlq;
using ConvivenciaPix.Infrastructure.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<DlqRetryOptions>(builder.Configuration.GetSection(DlqRetryOptions.SectionName));

// Only Kafka is needed here (no SQL Server, Redis or HSM), so the worker registers its own
// byte-level clients instead of calling AddInfrastructure.
var bootstrapServers = builder.Configuration["Kafka:BootstrapServers"]
    ?? throw new InvalidOperationException("Kafka:BootstrapServers is required.");

builder.Services.AddSingleton<ISpiMetrics, SpiMetrics>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IConsumer<byte[], byte[]>>(sp =>
{
    var options = sp.GetRequiredService<IOptions<DlqRetryOptions>>().Value;
    return new ConsumerBuilder<byte[], byte[]>(new ConsumerConfig
    {
        BootstrapServers = bootstrapServers,
        GroupId = "spi-dlq-retry",
        AutoOffsetReset = AutoOffsetReset.Earliest,
        EnableAutoCommit = false,
        // The consumer waits out a message's backoff between polls; keep it in the group meanwhile.
        MaxPollIntervalMs = (Math.Max(options.MaxDelaySeconds, options.RepublishFailureDelaySeconds) + 60) * 1000,
        SessionTimeoutMs = 45000
    }).Build();
});
builder.Services.AddSingleton<IProducer<byte[], byte[]>>(_ =>
    new ProducerBuilder<byte[], byte[]>(new ProducerConfig
    {
        BootstrapServers = bootstrapServers,
        Acks = Acks.All,
        EnableIdempotence = true
    }).Build());
builder.Services.AddHostedService<DlqRetryConsumer>();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("spi-dlq-retry-worker"))
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
