using ConvivenciaPix.V2.Domain;

namespace ConvivenciaPix.V2.Application.Interfaces;

public interface IProxyMetrics
{
    void Forwarded(SystemName system, int statusCode);
    void SentConflict(SystemName system, string msgType);
    void Routed(string msgType, string routeSource, string targets);
    void InboundDuplicate();
    void InboundQuarantined(string msgType, string reason);
}
