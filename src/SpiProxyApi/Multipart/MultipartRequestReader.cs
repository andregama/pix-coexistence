using ConvivenciaPix.Application.DTOs;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using System.Text;

namespace ConvivenciaPix.SpiProxyApi.Multipart;

public static class MultipartRequestReader
{
    public const int MaxParts = 10;

    /// <summary>Reads up to <see cref="MaxParts"/> parts. Returns null if the boundary header is missing or a part is invalid.</summary>
    public static async Task<IReadOnlyList<SpiInboundMessage>?> ReadAsync(
        HttpRequest request, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType))
            return null;
        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary)) return null;

        var reader = new MultipartReader(boundary, request.Body);
        var parts = new List<SpiInboundMessage>();

        while (true)
        {
            var section = await reader.ReadNextSectionAsync(cancellationToken);
            if (section is null) break;

            if (parts.Count >= MaxParts) return null;

            using var sr = new StreamReader(section.Body, Encoding.UTF8);
            var body = await sr.ReadToEndAsync(cancellationToken);
            parts.Add(new SpiInboundMessage(body, section.ContentType ?? "application/xml"));
        }

        return parts;
    }
}
