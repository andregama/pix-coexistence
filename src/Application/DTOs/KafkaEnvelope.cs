namespace ConvivenciaPix.Application.DTOs;

public sealed record KafkaEnvelope(
    string MessageId,
    string PayloadBase64,
    DateTimeOffset Timestamp,
    string? CorrelationId = null,
    string? SchemaVersion = "1.0");
