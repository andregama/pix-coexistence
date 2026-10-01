using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace ConvivenciaPix.V2.Infrastructure.Parsing;

public sealed record SpiMessagePart(byte[] Body, string? PiResourceId);

/// <summary>
/// Splits SPI HTTP bodies into their XML messages. The SPI API carries either one message
/// (application/xml) or several (multipart/mixed, one XML per part, each with its PI-ResourceId).
/// </summary>
public static class SpiMultipart
{
    public const int MaxParts = 10;

    public static bool IsXml(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && (parsed.MediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || parsed.MediaType.Equals("text/xml", StringComparison.OrdinalIgnoreCase));

    public static bool IsMultipart(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && parsed.MediaType.Equals("multipart/mixed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the parts of a multipart/mixed body. Returns null when the boundary is missing, the body
    /// is malformed or it holds more than <paramref name="maxParts"/> parts.
    /// </summary>
    public static async Task<IReadOnlyList<SpiMessagePart>?> ReadPartsAsync(
        byte[] body, string? contentType, int maxParts = MaxParts, CancellationToken cancellationToken = default)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed))
            return null;
        var boundary = HeaderUtilities.RemoveQuotes(parsed.Boundary).Value;
        if (string.IsNullOrEmpty(boundary))
            return null;

        var parts = new List<SpiMessagePart>();
        try
        {
            var reader = new MultipartReader(boundary, new MemoryStream(body, writable: false));
            while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
            {
                if (parts.Count >= maxParts)
                    return null;

                using var buffer = new MemoryStream();
                await section.Body.CopyToAsync(buffer, cancellationToken);

                var resourceId = section.Headers is not null && section.Headers.TryGetValue("PI-ResourceId", out var values)
                    ? values.ToString()
                    : null;
                parts.Add(new SpiMessagePart(buffer.ToArray(), string.IsNullOrWhiteSpace(resourceId) ? null : resourceId.Trim()));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }

        return parts;
    }
}
