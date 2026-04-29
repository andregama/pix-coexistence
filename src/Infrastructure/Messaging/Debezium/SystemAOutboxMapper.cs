using ConvivenciaPix.Application.DTOs;
using System.Text.Json;

namespace ConvivenciaPix.Infrastructure.Messaging.Debezium;

/// <summary>
/// Translates raw Debezium CDC JSON into application DTOs.
/// Versioned explicitly: add a new static method (MapV2, MapV3...) when System A's
/// outbox table schema changes. Never deserialize CDC output directly to domain objects.
/// </summary>
public static class SystemAOutboxMapper
{
    public static SpiResponseDto MapV1(string cdcJson)
    {
        using var doc = JsonDocument.Parse(cdcJson);
        var root = doc.RootElement;

        // Debezium wraps the row data under "after" for INSERT/UPDATE events
        var after = root.TryGetProperty("after", out var afterEl) ? afterEl : root;

        var idSystemA = GetRequiredString(after, "IdSystemA");
        var messageId = GetRequiredString(after, "MessageId");
        var payload = GetRequiredString(after, "Payload");

        string? errorCode = after.TryGetProperty("ErrorCode", out var ecEl) && ecEl.ValueKind != JsonValueKind.Null
            ? ecEl.GetString()
            : null;

        return new SpiResponseDto(
            MessageId: messageId,
            IdSystemA: idSystemA,
            IdSystemB: string.Empty, // resolved by proxy-worker via correlation DB lookup
            SignedXml: payload,
            ProcessedAt: DateTimeOffset.UtcNow,
            IsError: errorCode is not null,
            ErrorCode: errorCode);
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind == JsonValueKind.Null)
            throw new InvalidOperationException(
                $"Debezium CDC payload missing required field '{propertyName}'. " +
                "Check if System A's outbox schema changed and update the mapper.");

        return prop.GetString()
            ?? throw new InvalidOperationException($"Field '{propertyName}' was null in CDC payload.");
    }
}
