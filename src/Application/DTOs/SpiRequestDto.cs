namespace ConvivenciaPix.Application.DTOs;

public sealed record SpiRequestDto(
    string MessageId,
    string RawXml,
    string? CorrelationId,
    DateTimeOffset ReceivedAt,
    string? Ispb = null,
    string? PiResourceId = null);
