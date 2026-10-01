using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Domain;
using System.Diagnostics.Metrics;

namespace ConvivenciaPix.V2.Infrastructure.Metrics;

public sealed class ProxyMetrics : IProxyMetrics, IDisposable
{
    public const string MeterName = "ConvivenciaPix.V2";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _forwarded;
    private readonly Counter<long> _sentConflicts;
    private readonly Counter<long> _routed;
    private readonly Counter<long> _inboundDuplicates;
    private readonly Counter<long> _inboundQuarantined;

    public ProxyMetrics()
    {
        _forwarded = _meter.CreateCounter<long>(
            "spi_v2_forwarded_total", description: "Requests forwarded to the SPI, by system and HTTP status");
        _sentConflicts = _meter.CreateCounter<long>(
            "spi_v2_sent_conflicts_total", description: "Messages whose MsgId was already sent by the other system");
        _routed = _meter.CreateCounter<long>(
            "spi_v2_inbound_routed_total", description: "Inbound messages routed, by type, route source and targets");
        _inboundQuarantined = _meter.CreateCounter<long>(
            "spi_v2_inbound_quarantined_total", description: "Inbound messages stored but delivered to no system, by reason");
        _inboundDuplicates = _meter.CreateCounter<long>(
            "spi_v2_inbound_duplicates_total", description: "Inbound messages redelivered by the SPI and skipped");
    }

    public void Forwarded(SystemName system, int statusCode) =>
        _forwarded.Add(1, new("system", system.ToString()), new("status", statusCode));

    public void SentConflict(SystemName system, string msgType) =>
        _sentConflicts.Add(1, new("system", system.ToString()), new("msg_type", msgType));

    public void Routed(string msgType, string routeSource, string targets) =>
        _routed.Add(1, new("msg_type", msgType), new("route_source", routeSource), new("targets", targets));

    public void InboundDuplicate() => _inboundDuplicates.Add(1);

    public void InboundQuarantined(string msgType, string reason) =>
        _inboundQuarantined.Add(1, new("msg_type", msgType), new("reason", reason));

    public void Dispose() => _meter.Dispose();
}
