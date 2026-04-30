namespace ConvivenciaPix.Application.Interfaces;

public interface ISpiMetrics
{
    void RecordCorrelationSource(string source);
    void RecordProxyResponseLatency(double milliseconds);
    void RecordDiscrepancy(string field);
    void RecordDlqMessage(string topic);
}
