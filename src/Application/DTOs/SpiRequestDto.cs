namespace ConvivenciaPix.Application.DTOs;

public sealed record SpiRequestDto(
    string MessageId,
    string RawXml,
    string? CorrelationId,
    DateTimeOffset ReceivedAt);
