namespace ConvivenciaPix.Domain.Entities;

public sealed class SpiDiscrepancy
{
    public Guid Id { get; private set; }
    public string IdSystemA { get; private set; } = default!;
    public string IdSystemB { get; private set; } = default!;
    public string CorrelationSource { get; private set; } = default!;
    public string Field { get; private set; } = default!;
    public string? SystemAValue { get; private set; }
    public string? SystemBValue { get; private set; }
    public DateTime DetectedAt { get; private set; }

    private SpiDiscrepancy() { }

    public static SpiDiscrepancy Create(
        string idSystemA,
        string idSystemB,
        string correlationSource,
        string field,
        string? systemAValue,
        string? systemBValue)
    {
        return new SpiDiscrepancy
        {
            Id = Guid.NewGuid(),
            IdSystemA = idSystemA,
            IdSystemB = idSystemB,
            CorrelationSource = correlationSource,
            Field = field,
            SystemAValue = systemAValue,
            SystemBValue = systemBValue,
            DetectedAt = DateTime.UtcNow
        };
    }
}
