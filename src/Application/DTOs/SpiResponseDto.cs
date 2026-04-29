namespace ConvivenciaPix.Application.DTOs;

public sealed record SpiResponseDto(
    string MessageId,
    string IdSystemA,
    string IdSystemB,
    string SignedXml,
    DateTimeOffset ProcessedAt,
    bool IsError = false,
    string? ErrorCode = null);
