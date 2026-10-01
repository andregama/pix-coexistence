using ConvivenciaPix.V2.Domain.Messages;
using System.Security.Cryptography;
using System.Text;

namespace ConvivenciaPix.V2.SpiProxyApi.Multipart;

public static class MultipartResponseWriter
{
    private static readonly byte[] Crlf = "\r\n"u8.ToArray();

    /// <summary>
    /// Writes the messages as multipart/mixed, one XML per part with its PI-ResourceId. Message
    /// bytes are written exactly as received from Bacen, so its signatures stay valid.
    /// </summary>
    public static async Task WriteAsync(
        HttpResponse response, IReadOnlyList<DeliveryItem> messages, CancellationToken cancellationToken)
    {
        var boundary = "spi-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        response.ContentType = $"multipart/mixed; boundary=\"{boundary}\"";

        foreach (var message in messages)
        {
            var header = $"--{boundary}\r\nContent-Type: application/xml; charset=utf-8\r\nPI-ResourceId: {message.PiResourceId}\r\n\r\n";
            await response.Body.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken);
            await response.Body.WriteAsync(message.Payload, cancellationToken);
            await response.Body.WriteAsync(Crlf, cancellationToken);
        }

        await response.Body.WriteAsync(Encoding.ASCII.GetBytes($"--{boundary}--\r\n"), cancellationToken);
    }
}
