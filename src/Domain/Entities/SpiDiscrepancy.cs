namespace ConvivenciaPix.Domain.Entities;

public sealed class SpiDiscrepancy
{
    public Guid Id { get; private set; }
    public string IdempotentId { get; private set; } = default!;
    public string MsgType { get; private set; } = default!;
    public string Field { get; private set; } = default!;
    public string? SystemAValue { get; private set; }
    public string? SystemBValue { get; private set; }
    public DateTime DetectedAt { get; private set; }

    private SpiDiscrepancy() { }

    public static SpiDiscrepancy Create(
        string idempotentId,
        string msgType,
        string field,
        string? systemAValue,
        string? systemBValue)
    {
        return new SpiDiscrepancy
        {
            Id = Guid.NewGuid(),
            IdempotentId = idempotentId,
            MsgType = msgType,
            Field = field,
            SystemAValue = systemAValue,
            SystemBValue = systemBValue,
            DetectedAt = DateTime.UtcNow
        };
    }
}
