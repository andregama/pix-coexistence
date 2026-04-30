using ConvivenciaPix.Application.Interfaces;
using System.Diagnostics.Metrics;

namespace ConvivenciaPix.Infrastructure.Metrics;

public sealed class SpiMetrics : ISpiMetrics, IDisposable
{
    public const string MeterName = "ConvivenciaPix";

    private readonly Meter _meter;
    private readonly Counter<long> _correlationSourceCounter;
    private readonly Histogram<double> _responseLatencyHistogram;
    private readonly Counter<long> _discrepancyCounter;
    private readonly Counter<long> _dlqCounter;

    public SpiMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        _correlationSourceCounter = _meter.CreateCounter<long>(
            "spi.correlation.source",
            description: "Number of correlations by source (Orchestrator or Heuristic)");
        _responseLatencyHistogram = _meter.CreateHistogram<double>(
            "spi.proxy.response_latency_ms",
            unit: "ms",
            description: "End-to-end proxy response processing latency in milliseconds");
        _discrepancyCounter = _meter.CreateCounter<long>(
            "spi.discrepancies.total",
            description: "Total discrepancies detected between System A and System B by field");
        _dlqCounter = _meter.CreateCounter<long>(
            "spi.dlq.messages",
            description: "Total messages routed to DLQ topics");
    }

    public void RecordCorrelationSource(string source) =>
        _correlationSourceCounter.Add(1, new KeyValuePair<string, object?>("source", source));

    public void RecordProxyResponseLatency(double milliseconds) =>
        _responseLatencyHistogram.Record(milliseconds);

    public void RecordDiscrepancy(string field) =>
        _discrepancyCounter.Add(1, new KeyValuePair<string, object?>("field", field));

    public void RecordDlqMessage(string topic) =>
        _dlqCounter.Add(1, new KeyValuePair<string, object?>("topic", topic));

    public void Dispose() => _meter.Dispose();
}
