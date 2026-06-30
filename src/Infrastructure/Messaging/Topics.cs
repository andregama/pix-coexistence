namespace ConvivenciaPix.Infrastructure.Messaging;

public static class Topics
{
    public const string SystemBRequests = "spi.systemb.requests";
    public const string SystemBRequestsDlq = "spi.systemb.requests.dlq";

    public const string SystemAOutbound = "spi.systema.outbound";
    public const string SystemAOutboundDlq = "spi.systema.outbound.dlq";

    public const string SystemAInbound = "spi.systema.inbound";
    public const string SystemAInboundDlq = "spi.systema.inbound.dlq";

    public const string SystemBResponses = "spi.systemb.responses";
    public const string SystemBResponsesDlq = "spi.systemb.responses.dlq";

    public const string CorrelationEvents = "spi.correlation.events";
    public const string CorrelationEventsDlq = "spi.correlation.events.dlq";

    public const string ComparisonEvents = "spi.comparison.events";
    public const string ComparisonEventsDlq = "spi.comparison.events.dlq";

    public const string Discrepancies = "spi.discrepancies";

    public static string DlqFor(string topic) => $"{topic}.dlq";
}
