using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace ConvivenciaPix.V2.BacenSpiSimulator;

public sealed class SimulatorOptions
{
    /// <summary>How long a pull waits for a message before answering 204.</summary>
    public int LongPollSeconds { get; set; } = 3;

    public int MaxMessagesPerMultipart { get; set; } = 10;

    /// <summary>Generate the SPI's replies (pacs.002 for pacs.008, pibr.002 for pibr.001).</summary>
    public bool AutoReply { get; set; } = true;
}

/// <summary>
/// A stand-in for the real SPI API for local development and tests: accepts what a participant
/// sends, optionally replies like the SPI would, and serves the pull stream with the SPI's
/// acknowledgement semantics. The /sim endpoints let a developer inject or inspect messages.
/// </summary>
public static class SimulatorHost
{
    /// <summary>A body containing this marker is refused with HTTP 400, like a malformed request.</summary>
    public const string HttpErrorMarker = "SIM-HTTP-400";

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection("Simulator"));
        builder.Services.AddSingleton<SimulatorState>();
        configure?.Invoke(builder);

        var app = builder.Build();
        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SimulatorOptions>>().Value;
        var state = app.Services.GetRequiredService<SimulatorState>();

        app.MapPost("/api/v1/in/{ispb}/msgs", async (HttpRequest request, CancellationToken ct) =>
        {
            var body = await ReadAllAsync(request.Body, ct);
            if (Encoding.UTF8.GetString(body).Contains(HttpErrorMarker))
            {
                return Results.Text(
                    "<problem xmlns=\"urn:ietf:rfc:7807\"><type>https://www.bcb.gov.br/pi/api/v1/error/BadRequest</type><title>Bad Request</title><status>400</status></problem>",
                    "application/problem+xml", Encoding.UTF8, StatusCodes.Status400BadRequest);
            }

            var messages = await SplitAsync(body, request.ContentType, ct);
            if (messages.Count == 0)
                return Results.BadRequest();

            var resourceIds = new List<string>();
            foreach (var message in messages)
            {
                var resourceId = SimulatorState.NewResourceId();
                resourceIds.Add(resourceId);

                var (msgType, msgId) = SimulatedReplies.Identify(message);
                state.Received.Enqueue(new ReceivedMessage(resourceId, msgType, msgId, message));

                if (options.AutoReply && SimulatedReplies.For(message) is { } reply)
                    state.Enqueue(reply);
            }

            request.HttpContext.Response.Headers["PI-ResourceId"] = string.Join(",", resourceIds);
            return Results.StatusCode(StatusCodes.Status201Created);
        });

        app.MapGet("/api/v1/out/{ispb}/stream/start", (string ispb, HttpContext context, CancellationToken ct) =>
        {
            state.RequeueUnacked();
            return PullAsync(ispb, context, state, options, ct);
        });

        app.MapGet("/api/v1/out/{ispb}/stream/{streamId}", (string ispb, string streamId, HttpContext context, CancellationToken ct) =>
        {
            state.Ack(streamId);
            return PullAsync(ispb, context, state, options, ct);
        });

        app.MapDelete("/api/v1/out/{ispb}/stream/{streamId}", (string streamId) =>
        {
            state.Ack(streamId);
            return Results.Ok();
        });

        // --- developer endpoints -------------------------------------------------------------

        // Queue a message as if Bacen had sent it. Pass ?piResourceId= to simulate a redelivery.
        app.MapPost("/sim/inbound", async (HttpRequest request, string? piResourceId, CancellationToken ct) =>
        {
            var body = await ReadAllAsync(request.Body, ct);
            return body.Length == 0
                ? Results.BadRequest()
                : Results.Ok(new { piResourceId = state.Enqueue(body, piResourceId) });
        });

        app.MapGet("/sim/sent", () => state.Received.Select(m => new { m.PiResourceId, m.MsgType, m.MsgId, size = m.Body.Length }));

        app.MapGet("/sim/status", () => new { pending = state.PendingCount, unacked = state.UnackedCount, received = state.Received.Count });

        return app;
    }

    private static async Task PullAsync(
        string ispb, HttpContext context, SimulatorState state, SimulatorOptions options, CancellationToken ct)
    {
        var multipart = context.Request.Headers.Accept.ToString().Contains("multipart/mixed", StringComparison.OrdinalIgnoreCase);
        var nextStreamId = SimulatorState.NewStreamId();
        var batch = await state.TakeAsync(
            nextStreamId,
            multipart ? options.MaxMessagesPerMultipart : 1,
            TimeSpan.FromSeconds(options.LongPollSeconds),
            ct);

        var response = context.Response;
        response.Headers["PI-Pull-Next"] = $"/api/v1/out/{ispb}/stream/{nextStreamId}";

        if (batch.Count == 0)
        {
            response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        response.StatusCode = StatusCodes.Status200OK;
        if (!multipart)
        {
            response.Headers["PI-ResourceId"] = batch[0].PiResourceId;
            response.ContentType = "application/xml; charset=utf-8";
            await response.Body.WriteAsync(batch[0].Body, ct);
            return;
        }

        var boundary = "sim-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        response.ContentType = $"multipart/mixed; boundary=\"{boundary}\"";
        foreach (var message in batch)
        {
            var header = $"--{boundary}\r\nContent-Type: application/xml; charset=utf-8\r\nPI-ResourceId: {message.PiResourceId}\r\n\r\n";
            await response.Body.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            await response.Body.WriteAsync(message.Body, ct);
            await response.Body.WriteAsync("\r\n"u8.ToArray(), ct);
        }
        await response.Body.WriteAsync(Encoding.ASCII.GetBytes($"--{boundary}--\r\n"), ct);
    }

    private static async Task<List<byte[]>> SplitAsync(byte[] body, string? contentType, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            || !parsed.MediaType.Equals("multipart/mixed", StringComparison.OrdinalIgnoreCase))
        {
            return body.Length == 0 ? [] : [body];
        }

        var boundary = HeaderUtilities.RemoveQuotes(parsed.Boundary).Value;
        if (string.IsNullOrEmpty(boundary))
            return [];

        var messages = new List<byte[]>();
        var reader = new MultipartReader(boundary, new MemoryStream(body));
        while (await reader.ReadNextSectionAsync(ct) is { } section)
            messages.Add(await ReadAllAsync(section.Body, ct));
        return messages;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }
}
