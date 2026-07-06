namespace ConvivenciaPix.Infrastructure.Messaging;

public static class Topics
{
    public const string SystemBRequests = "spi.systemb.requests";
    public const string SystemBRequestsDlq = "spi.systemb.requests.dlq";

    // Both System A tables (SpiRecepApiBacen inbound, SpiEnvioApiBacen outbound) are routed by
    // Debezium to this single topic, matching the production configuration. The correlate worker
    // dispatches by the CDC source.table. Overridable via Kafka:SystemACdcTopic.
    public const string SystemACdc = "spi.systema.cdc";
    public const string SystemACdcDlq = "spi.systema.cdc.dlq";

    public const string SystemBResponses = "spi.systemb.responses";
    public const string SystemBResponsesDlq = "spi.systemb.responses.dlq";

    public const string CorrelationEvents = "spi.correlation.events";
    public const string CorrelationEventsDlq = "spi.correlation.events.dlq";

    public const string ComparisonEvents = "spi.comparison.events";
    public const string ComparisonEventsDlq = "spi.comparison.events.dlq";

    public const string Discrepancies = "spi.discrepancies";

    public static string DlqFor(string topic) => $"{topic}.dlq";
}
