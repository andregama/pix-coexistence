using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Infrastructure.Parsing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace ConvivenciaPix.V2.Infrastructure.Simulation;

public sealed class SimulatorOptions
{
    /// <summary>How long a pull waits for a message before answering 204.</summary>
    public int LongPollSeconds { get; set; } = 3;

    public int MaxMessagesPerMultipart { get; set; } = 10;

    /// <summary>Generate the SPI's replies (pacs.002 for pacs.008, pibr.002 for pibr.001).</summary>
    public bool AutoReply { get; set; } = true;
}

/// <summary>
/// A stand-in for the real SPI API: accepts what a participant sends, optionally replies like the
/// SPI would, and serves the pull stream with the SPI's acknowledgement semantics. It answers the
/// SPI's requests wherever they come from — over HTTP in the BacenSpiSimulator tool, or inside the
/// proxy's own process through <see cref="Gateway.SimulatedSpiGateway"/>.
/// </summary>
public sealed class SpiSimulator(SimulatorState state, IOptions<SimulatorOptions> options)
{
    /// <summary>A body containing this marker is refused with HTTP 400, like a malformed request.</summary>
    public const string HttpErrorMarker = "SIM-HTTP-400";

    private const string BadRequestProblem =
        "<problem xmlns=\"urn:ietf:rfc:7807\"><type>https://www.bcb.gov.br/pi/api/v1/error/BadRequest</type><title>Bad Request</title><status>400</status></problem>";

    private static readonly IReadOnlyDictionary<string, string[]> NoHeaders = new Dictionary<string, string[]>();

    private readonly SimulatorOptions _options = options.Value;

    /// <summary>Answers one request to the SPI API (<c>/api/v1/in/…</c> or <c>/api/v1/out/…</c>).</summary>
    public async Task<SpiGatewayResponse> HandleAsync(
        string method, string path, string? accept, string? contentType, byte[] body, CancellationToken cancellationToken)
    {
        var segments = path.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments is ["api", "v1", "in", _, "msgs"] && method == "POST")
            return await ReceiveAsync(body, contentType, cancellationToken);

        if (segments is ["api", "v1", "out", var ispb, "stream", var streamId])
        {
            var start = streamId == "start";
            switch (method)
            {
                case "GET":
                    // A new stream means the previous one was abandoned; following a stream id
                    // confirms the batch delivered under it.
                    if (start) state.RequeueUnacked(); else state.Ack(streamId);
                    return await PullAsync(ispb, accept, cancellationToken);
                case "DELETE" when !start:
                    state.Ack(streamId);
                    return Status(StatusCodes.Status200OK);
            }
        }

        return Status(StatusCodes.Status404NotFound);
    }

    private async Task<SpiGatewayResponse> ReceiveAsync(byte[] body, string? contentType, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetString(body).Contains(HttpErrorMarker))
        {
            return new SpiGatewayResponse(
                StatusCodes.Status400BadRequest, NoHeaders, "application/problem+xml; charset=utf-8", Encoding.UTF8.GetBytes(BadRequestProblem));
        }

        var messages = await SplitAsync(body, contentType, cancellationToken);
        if (messages.Count == 0)
            return Status(StatusCodes.Status400BadRequest);

        var resourceIds = new List<string>();
        foreach (var message in messages)
        {
            var resourceId = SimulatorState.NewResourceId();
            resourceIds.Add(resourceId);

            var (msgType, msgId) = SimulatedReplies.Identify(message);
            state.Received.Enqueue(new ReceivedMessage(resourceId, msgType, msgId, message));

            if (_options.AutoReply && SimulatedReplies.For(message) is { } reply)
                state.Enqueue(reply);
        }

        return new SpiGatewayResponse(
            StatusCodes.Status201Created,
            new Dictionary<string, string[]> { ["PI-ResourceId"] = [string.Join(",", resourceIds)] },
            null,
            []);
    }

    private async Task<SpiGatewayResponse> PullAsync(string ispb, string? accept, CancellationToken cancellationToken)
    {
        var multipart = accept?.Contains("multipart/mixed", StringComparison.OrdinalIgnoreCase) == true;
        var nextStreamId = SimulatorState.NewStreamId();
        var batch = await state.TakeAsync(
            nextStreamId,
            multipart ? _options.MaxMessagesPerMultipart : 1,
            TimeSpan.FromSeconds(_options.LongPollSeconds),
            cancellationToken);

        var headers = new Dictionary<string, string[]> { ["PI-Pull-Next"] = [$"/api/v1/out/{ispb}/stream/{nextStreamId}"] };

        if (batch.Count == 0)
            return new SpiGatewayResponse(StatusCodes.Status204NoContent, headers, null, []);

        if (!multipart)
        {
            headers["PI-ResourceId"] = [batch[0].PiResourceId];
            return new SpiGatewayResponse(StatusCodes.Status200OK, headers, "application/xml; charset=utf-8", batch[0].Body);
        }

        var boundary = "sim-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        using var content = new MemoryStream();
        foreach (var message in batch)
        {
            content.Write(Encoding.ASCII.GetBytes(
                $"--{boundary}\r\nContent-Type: application/xml; charset=utf-8\r\nPI-ResourceId: {message.PiResourceId}\r\n\r\n"));
            content.Write(message.Body);
            content.Write("\r\n"u8);
        }
        content.Write(Encoding.ASCII.GetBytes($"--{boundary}--\r\n"));

        return new SpiGatewayResponse(
            StatusCodes.Status200OK, headers, $"multipart/mixed; boundary=\"{boundary}\"", content.ToArray());
    }

    private static async Task<IReadOnlyList<byte[]>> SplitAsync(byte[] body, string? contentType, CancellationToken cancellationToken)
    {
        if (!SpiMultipart.IsMultipart(contentType))
            return body.Length == 0 ? [] : [body];

        var parts = await SpiMultipart.ReadPartsAsync(body, contentType, int.MaxValue, cancellationToken);
        return parts is null ? [] : parts.Select(part => part.Body).ToList();
    }

    private static SpiGatewayResponse Status(int statusCode) => new(statusCode, NoHeaders, null, []);
}

public static class SimulatorEndpoints
{
    /// <summary>
    /// The developer endpoints of the simulator: inject a message as if Bacen had sent it, and
    /// inspect what the participant sent.
    /// </summary>
    public static RouteGroupBuilder MapSimulatorDevEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/sim");

        // Queue a message as if Bacen had sent it. Pass ?piResourceId= to simulate a redelivery.
        group.MapPost("/inbound", async (HttpRequest request, string? piResourceId, SimulatorState state, CancellationToken ct) =>
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            return buffer.Length == 0
                ? Results.BadRequest()
                : Results.Ok(new { piResourceId = state.Enqueue(buffer.ToArray(), piResourceId) });
        });

        group.MapGet("/sent", (SimulatorState state) =>
            state.Received.Select(m => new { m.PiResourceId, m.MsgType, m.MsgId, size = m.Body.Length }));

        group.MapGet("/status", (SimulatorState state) =>
            new { pending = state.PendingCount, unacked = state.UnackedCount, received = state.Received.Count });

        return group;
    }
}
