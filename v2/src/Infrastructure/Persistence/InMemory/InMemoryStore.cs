using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Domain.Messages;
using ConvivenciaPix.V2.Domain.Repositories;
using ConvivenciaPix.V2.Domain.Routing;

namespace ConvivenciaPix.V2.Infrastructure.Persistence.InMemory;

/// <summary>
/// The "tables" of the in-memory storage provider, mirroring <c>infra/sql</c>. Every repository
/// works under <see cref="Gate"/>, which gives each operation the atomicity the SQL statements and
/// transactions give in the real database.
/// </summary>
internal sealed class InMemoryStore(TimeProvider time)
{
    private long _sequence;

    public object Gate { get; } = new();

    public DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    /// <summary>Insertion order, standing in for the IDENTITY columns.</summary>
    public long NextSequence() => ++_sequence;

    // SPI ids are case-sensitive, as in the binary collation of the SQL columns.
    public Dictionary<string, SentRouteRow> SentRoutes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, SentRouteRow> SentRoutesByResourceId { get; } = new(StringComparer.Ordinal);
    public Dictionary<TxKey, AffinityRow> Affinities { get; } = [];
    public Dictionary<string, InboundRow> Inbound { get; } = new(StringComparer.Ordinal);

    /// <summary>Unacknowledged deliveries per system, oldest first.</summary>
    public Dictionary<SystemName, List<DeliveryRow>> Pending { get; } =
        Enum.GetValues<SystemName>().ToDictionary(system => system, _ => new List<DeliveryRow>());

    public List<AuditEvent> Outbox { get; } = [];
    public List<SystemBDocumentEntry> Documents { get; } = [];
}

internal sealed class SentRouteRow(SpiSentRoute route, long sequence, DateTime createdAt)
{
    public SpiSentRoute Route { get; } = route;
    public long Sequence { get; } = sequence;
    public DateTime CreatedAt { get; } = createdAt;
    public string? PiResourceId { get; set; }
    public int? HttpStatus { get; set; }
    public bool EchoAnswered { get; set; }
}

internal sealed record AffinityRow(SystemName System, string Origin, DateTime CreatedAt);

internal sealed class InboundRow(SpiInboundMsg message, DateTime receivedAt)
{
    public SpiInboundMsg Message { get; } = message;
    public DateTime ReceivedAt { get; } = receivedAt;

    /// <summary>Deliveries still waiting for an ack; the message is kept while there is any.</summary>
    public int Unacked { get; set; }
}

internal sealed class DeliveryRow(InboundRow message, byte[] payload)
{
    public InboundRow Message { get; } = message;
    public byte[] Payload { get; } = payload;
    public string? LeaseStreamId { get; set; }
    public DateTime? LeaseUntil { get; set; }
}
