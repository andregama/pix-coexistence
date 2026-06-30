namespace ConvivenciaPix.Application.DTOs;

public sealed record SpiComparisonEventDto(
    string IdempotentId,
    string MsgType,
    string SystemAXml,
    string SystemBXml,
    DateTimeOffset OccurredAt);
