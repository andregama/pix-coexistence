namespace ConvivenciaPix.Domain.Events;

public sealed record SpiDiscrepancyDetected(
    string IdempotentId,
    string MsgType,
    IReadOnlyList<DiscrepancyRecord> Discrepancies,
    DateTimeOffset DetectedAt);

public sealed record DiscrepancyRecord(
    string FieldName,
    string? ValueA,
    string? ValueB);
