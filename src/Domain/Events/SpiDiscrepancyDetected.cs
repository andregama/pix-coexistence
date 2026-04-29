namespace ConvivenciaPix.Domain.Events;

public sealed record SpiDiscrepancyDetected(
    string IdSystemA,
    string IdSystemB,
    string CorrelationSource,
    IReadOnlyList<DiscrepancyRecord> Discrepancies,
    DateTimeOffset DetectedAt);

public sealed record DiscrepancyRecord(
    string FieldName,
    string? ValueA,
    string? ValueB);
