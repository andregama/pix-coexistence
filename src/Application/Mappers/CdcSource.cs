using System.Text.Json;

namespace ConvivenciaPix.Application.Mappers;

/// <summary>
/// Reads the originating table from a Debezium change envelope. Both System A tables are routed to
/// a single Kafka topic in production; the correlate worker uses <see cref="ExtractTable"/> to
/// dispatch each event to the inbound or outbound flow.
/// </summary>
public static class CdcSource
{
    /// <summary>System A inbound table (SPI → PSP); maps to the inbound correlation flow.</summary>
    public const string InboundTable = "SpiRecepApiBacen";

    /// <summary>System A outbound table (PSP → SPI); maps to the outbound correlation flow.</summary>
    public const string OutboundTable = "SpiEnvioApiBacen";

    /// <summary>
    /// Returns <c>source.table</c> from a Debezium envelope, or null for tombstones / payloads
    /// without a source block.
    /// </summary>
    public static string? ExtractTable(string? cdcJson)
    {
        if (string.IsNullOrWhiteSpace(cdcJson))
            return null;

        using var doc = JsonDocument.Parse(cdcJson);
        return doc.RootElement.TryGetProperty("source", out var source)
               && source.ValueKind == JsonValueKind.Object
               && source.TryGetProperty("table", out var table)
               && table.ValueKind == JsonValueKind.String
            ? table.GetString()
            : null;
    }
}
