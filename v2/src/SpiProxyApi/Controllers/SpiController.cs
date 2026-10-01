using ConvivenciaPix.V2.Application.UseCases.ForwardMessages;
using ConvivenciaPix.V2.Application.UseCases.PullStream;
using ConvivenciaPix.V2.Infrastructure.Gateway;
using ConvivenciaPix.V2.Infrastructure.Parsing;
using ConvivenciaPix.V2.SpiProxyApi.Listeners;
using ConvivenciaPix.V2.SpiProxyApi.Multipart;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ConvivenciaPix.V2.SpiProxyApi.Controllers;

public sealed class SpiOptions
{
    /// <summary>ISPB of the bank. When set, requests for any other ISPB are refused.</summary>
    public string Ispb { get; set; } = string.Empty;
}

/// <summary>
/// The SPI API as both systems see it. Sending is forwarded to the real SPI; pulling is served
/// from the calling system's own queue of messages routed to it.
/// </summary>
[ApiController]
[Authorize]
public sealed class SpiController : ControllerBase
{
    private readonly IForwardMessagesUseCase _forwardUseCase;
    private readonly IPullStreamUseCase _pullUseCase;
    private readonly IAckStreamUseCase _ackUseCase;
    private readonly SpiOptions _options;

    public SpiController(
        IForwardMessagesUseCase forwardUseCase,
        IPullStreamUseCase pullUseCase,
        IAckStreamUseCase ackUseCase,
        IOptions<SpiOptions> options)
    {
        _forwardUseCase = forwardUseCase;
        _pullUseCase = pullUseCase;
        _ackUseCase = ackUseCase;
        _options = options.Value;
    }

    /// <summary>
    /// Bacen SPI manual §2.2.1 — the system sends one (application/xml) or up to 10 (multipart/mixed)
    /// messages. They are forwarded to the SPI as received and the SPI's answer is returned as is.
    /// </summary>
    [HttpPost("api/v1/in/{ispb}/msgs")]
    public async Task<IActionResult> PostMessages(string ispb, CancellationToken cancellationToken)
    {
        if (!IsOwnIspb(ispb)) return Forbid();

        var contentType = Request.ContentType ?? string.Empty;
        var body = await ReadBodyAsync(cancellationToken);

        IReadOnlyList<ReadOnlyMemory<byte>> messages;
        if (SpiMultipart.IsXml(contentType))
        {
            if (body.Length == 0) return BadRequest();
            messages = [body];
        }
        else if (SpiMultipart.IsMultipart(contentType))
        {
            var parts = await SpiMultipart.ReadPartsAsync(body, contentType, SpiMultipart.MaxParts, cancellationToken);
            if (parts is null || parts.Count == 0) return BadRequest();
            messages = parts.Select(p => (ReadOnlyMemory<byte>)p.Body).ToList();
        }
        else
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        var headers = Request.Headers
            .Where(h => SpiForwardHeaders.IsForwardableRequestHeader(h.Key))
            .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        var result = await _forwardUseCase.ExecuteAsync(
            new ForwardRequest(HttpContext.GetCallerSystem(), ispb, contentType, body, messages, headers),
            cancellationToken);

        Response.StatusCode = result.StatusCode;
        foreach (var (name, values) in result.Headers)
        {
            if (SpiForwardHeaders.IsForwardableResponseHeader(name))
                Response.Headers[name] = values;
        }

        if (result.Body.Length > 0)
        {
            if (!string.IsNullOrEmpty(result.ContentType))
                Response.ContentType = result.ContentType;
            await Response.Body.WriteAsync(result.Body, cancellationToken);
        }

        return new EmptyResult();
    }

    /// <summary>Bacen SPI manual §2.2.2 — start a pull stream and long-poll for messages.</summary>
    [HttpGet("api/v1/out/{ispb}/stream/start")]
    public Task<IActionResult> StartStream(string ispb, CancellationToken cancellationToken) =>
        PullAsync(streamId: null, ispb, cancellationToken);

    /// <summary>Bacen SPI manual §2.2.2.2 — continue the stream; the previous batch is implicitly acked.</summary>
    [HttpGet("api/v1/out/{ispb}/stream/{streamId}")]
    public Task<IActionResult> ContinueStream(string ispb, string streamId, CancellationToken cancellationToken) =>
        PullAsync(streamId, ispb, cancellationToken);

    /// <summary>Bacen SPI manual §2.2.2.3 — close the stream, acking the last delivered batch.</summary>
    [HttpDelete("api/v1/out/{ispb}/stream/{streamId}")]
    public async Task<IActionResult> AckStream(string ispb, string streamId, CancellationToken cancellationToken)
    {
        if (!IsOwnIspb(ispb)) return Forbid();

        await _ackUseCase.ExecuteAsync(HttpContext.GetCallerSystem(), streamId, cancellationToken);
        return Ok();
    }

    private async Task<IActionResult> PullAsync(string? streamId, string ispb, CancellationToken cancellationToken)
    {
        if (!IsOwnIspb(ispb)) return Forbid();

        var multipart = Request.Headers.Accept.ToString().Contains("multipart/mixed", StringComparison.OrdinalIgnoreCase);
        var result = await _pullUseCase.ExecuteAsync(HttpContext.GetCallerSystem(), streamId, multipart, cancellationToken);

        Response.Headers["PI-Pull-Next"] = $"/api/v1/out/{ispb}/stream/{result.NextStreamId}";

        if (result.Messages.Count == 0)
            return StatusCode(StatusCodes.Status204NoContent);

        Response.Headers["PI-ResourceId"] = string.Join(",", result.Messages.Select(m => m.PiResourceId));
        Response.StatusCode = StatusCodes.Status200OK;

        if (multipart)
        {
            await MultipartResponseWriter.WriteAsync(Response, result.Messages, cancellationToken);
            return new EmptyResult();
        }

        // Without multipart the batch holds exactly one message.
        Response.ContentType = "application/xml; charset=utf-8";
        await Response.Body.WriteAsync(result.Messages[0].Payload, cancellationToken);
        return new EmptyResult();
    }

    private bool IsOwnIspb(string ispb) =>
        string.IsNullOrEmpty(_options.Ispb) || string.Equals(_options.Ispb, ispb, StringComparison.OrdinalIgnoreCase);

    private async Task<byte[]> ReadBodyAsync(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
