using ConvivenciaPix.Application.DTOs;
using System.Security.Cryptography;
using System.Text;

namespace ConvivenciaPix.SpiProxyApi.Multipart;

public static class MultipartResponseWriter
{
    public static async Task WriteAsync(
        HttpResponse response,
        IReadOnlyList<OutboundMessage> messages,
        CancellationToken cancellationToken)
    {
        var boundary = "spi-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        response.ContentType = $"multipart/mixed; boundary=\"{boundary}\"";

        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            sb.Append("--").Append(boundary).Append("\r\n");
            sb.Append("Content-Type: application/xml; charset=utf-8\r\n");
            sb.Append("PI-ResourceId: ").Append(msg.PiResourceId).Append("\r\n");
            sb.Append("\r\n");
            sb.Append(msg.SignedXml).Append("\r\n");
        }
        sb.Append("--").Append(boundary).Append("--\r\n");

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        await response.Body.WriteAsync(bytes, cancellationToken);
    }
}
