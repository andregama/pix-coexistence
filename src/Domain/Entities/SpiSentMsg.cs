using ConvivenciaPix.Domain.ValueObjects;

namespace ConvivenciaPix.Domain.Entities;

public sealed class SpiSentMsg
{
    public string IdSystemA { get; private set; }
    public string IdSystemB { get; private set; }
    public CorrelationSource CorrelationSource { get; private set; }
    public DateTime CreatedAt { get; private set; }

    // EF Core constructor
    private SpiSentMsg() { IdSystemA = null!; IdSystemB = null!; CorrelationSource = null!; }

    private SpiSentMsg(string idSystemA, string idSystemB, CorrelationSource correlationSource, DateTime createdAt)
    {
        IdSystemA = idSystemA;
        IdSystemB = idSystemB;
        CorrelationSource = correlationSource;
        CreatedAt = createdAt;
    }

    public static SpiSentMsg Create(string idSystemA, string idSystemB, CorrelationSource correlationSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idSystemA);
        ArgumentException.ThrowIfNullOrWhiteSpace(idSystemB);
        ArgumentNullException.ThrowIfNull(correlationSource);

        return new SpiSentMsg(idSystemA, idSystemB, correlationSource, DateTime.UtcNow);
    }
}
