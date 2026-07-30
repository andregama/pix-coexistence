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
    private readonly Histogram<double> _dictProxyLatencyHistogram;
    private readonly Counter<long> _dictUpstreamStatusCounter;

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
        _dictProxyLatencyHistogram = _meter.CreateHistogram<double>(
            "dict.proxy.request_latency_ms",
            unit: "ms",
            description: "End-to-end DICT proxy request latency (re-sign + forward + re-sign) in milliseconds");
        _dictUpstreamStatusCounter = _meter.CreateCounter<long>(
            "dict.proxy.upstream_status",
            description: "DICT proxy responses by upstream HTTP status code");
    }

    public void RecordCorrelationSource(string source) =>
        _correlationSourceCounter.Add(1, new KeyValuePair<string, object?>("source", source));

    public void RecordProxyResponseLatency(double milliseconds) =>
        _responseLatencyHistogram.Record(milliseconds);

    public void RecordDiscrepancy(string field) =>
        _discrepancyCounter.Add(1, new KeyValuePair<string, object?>("field", field));

    public void RecordDlqMessage(string topic) =>
        _dlqCounter.Add(1, new KeyValuePair<string, object?>("topic", topic));

    public void RecordDictProxyLatency(double milliseconds) =>
        _dictProxyLatencyHistogram.Record(milliseconds);

    public void RecordDictUpstreamStatus(int statusCode) =>
        _dictUpstreamStatusCounter.Add(1, new KeyValuePair<string, object?>("status", statusCode));

    public void Dispose() => _meter.Dispose();
}
