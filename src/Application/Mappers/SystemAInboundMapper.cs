using System.Text.Json;

namespace ConvivenciaPix.Application.Mappers;

/// <summary>
/// Translates Debezium CDC JSON from SpiRecepApiBacen (System A inbound table) into fields
/// needed by the correlation worker and proxy worker. The "after" property holds the row.
/// </summary>
public static class SystemAInboundMapper
{
    public sealed record Result(string XmlMsg, string? Problem);

    public static Result Map(string cdcJson)
    {
        using var doc = JsonDocument.Parse(cdcJson);
        var root = doc.RootElement;
        var after = root.TryGetProperty("after", out var afterEl) ? afterEl : root;

        return new Result(
            XmlMsg: GetRequiredString(after, "XmlMsg"),
            Problem: GetNullableString(after, "Problem"));
    }

    private static string GetRequiredString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            throw new InvalidOperationException(
                $"SpiRecepApiBacen CDC payload missing required field '{name}'.");
        return prop.GetString()
            ?? throw new InvalidOperationException($"Field '{name}' was null in CDC payload.");
    }

    private static string? GetNullableString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var prop) && prop.ValueKind != JsonValueKind.Null
            ? prop.GetString()
            : null;
}
