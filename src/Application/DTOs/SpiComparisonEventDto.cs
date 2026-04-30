namespace ConvivenciaPix.Application.DTOs;

public sealed record SpiComparisonEventDto(
    string IdSystemA,
    string IdSystemB,
    string SystemAXml,
    string SystemBXml,
    string CorrelationSource,
    DateTimeOffset OccurredAt);
