using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Domain.Messages;
using System.Text.Json;

namespace ConvivenciaPix.V2.Application.Audit;

/// <summary>Builds the audit events published to Kafka. Events carry identifiers only — no payload.</summary>
public static class AuditEvents
{
    public const string SentTopic = "v2.spi.audit.sent";
    public const string ReceivedTopic = "v2.spi.audit.received";

    public static AuditEvent Sent(
        string msgId, string msgType, SystemName system, string? txKey, string? piResourceId, int httpStatus) =>
        new(SentTopic, msgId, JsonSerializer.Serialize(new
        {
            msgId,
            msgType,
            system = system.ToString(),
            txKey,
            piResourceId,
            httpStatus,
            timestamp = DateTimeOffset.UtcNow
        }));

    public static AuditEvent Received(SpiInboundMsg message) =>
        new(ReceivedTopic, message.PiResourceId, JsonSerializer.Serialize(new
        {
            piResourceId = message.PiResourceId,
            msgId = message.MsgId,
            msgType = message.MsgType,
            txKey = message.TxKey,
            originalMsgId = message.OriginalMsgId,
            routeSource = message.RouteSource,
            targets = message.Targets,
            timestamp = DateTimeOffset.UtcNow
        }));
}
