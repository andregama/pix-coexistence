using System.IO.Compression;
using System.Text;

namespace ConvivenciaPix.Infrastructure.Hsm;

/// <summary>
/// Gzip compression for PIX request bodies. When <c>UseGzip</c> is enabled, the Dinamo SDK adds the
/// Content-Encoding/Accept-Encoding headers and auto-decompresses responses, but the request payload
/// must be compressed by the caller — this helper does that.
/// </summary>
internal static class PixGzip
{
    public static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(data, 0, data.Length);
        return output.ToArray();
    }
}

/// <summary>HTTP verb for a Dinamo PIX mTLS request (postPIX/putPIX/getPIX/deletePIX).</summary>
public enum PixHttpMethod
{
    Get,
    Post,
    Put,
    Delete
}

/// <summary>
/// The response of a Dinamo PIX mTLS HTTP request, mapped from the SDK's <c>PIXResponse</c> plus
/// <c>getPIXHTTPReqCode()</c>/<c>getPIXHTTPReqDetails()</c> read on the same session.
/// </summary>
public sealed record PixHttpResponse(
    int StatusCode,
    IReadOnlyDictionary<string, string[]> Headers,
    string? ContentType,
    byte[] Body);

/// <summary>
/// Parses the raw HTTP response-header block returned in <c>PIXResponse.Header</c> (a libcurl-style
/// buffer: an optional <c>HTTP/x</c> status line followed by <c>Name: Value</c> lines, CRLF-separated).
/// </summary>
internal static class PixHeaderParser
{
    public static (IReadOnlyDictionary<string, string[]> Headers, string? ContentType) Parse(byte[]? headerBlock)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (headerBlock is null || headerBlock.Length == 0)
            return (Empty(headers), null);

        var text = Encoding.UTF8.GetString(headerBlock);
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Status line(s) such as "HTTP/1.1 200 OK" carry no colon-delimited value.
            if (line.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)) continue;

            var idx = line.IndexOf(':');
            if (idx <= 0) continue;

            var name = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (!headers.TryGetValue(name, out var values))
                headers[name] = values = [];
            values.Add(value);
        }

        var contentType = headers.TryGetValue("Content-Type", out var ct) ? ct.FirstOrDefault() : null;
        return (Empty(headers), contentType);
    }

    private static IReadOnlyDictionary<string, string[]> Empty(Dictionary<string, List<string>> src) =>
        src.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
}
