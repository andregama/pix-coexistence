using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.UseCases.PullStream;
using ConvivenciaPix.Application.UseCases.ReceiveSpiRequest;
using ConvivenciaPix.SpiProxyApi.Multipart;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using System.Text;

namespace ConvivenciaPix.SpiProxyApi.Controllers;

[ApiController]
[Authorize]
public sealed class SpiController : ControllerBase
{
    private readonly IReceiveSpiRequestUseCase _receiveUseCase;
    private readonly IPullStreamUseCase _pullUseCase;
    private readonly IAckStreamUseCase _ackUseCase;

    public SpiController(
        IReceiveSpiRequestUseCase receiveUseCase,
        IPullStreamUseCase pullUseCase,
        IAckStreamUseCase ackUseCase)
    {
        _receiveUseCase = receiveUseCase;
        _pullUseCase = pullUseCase;
        _ackUseCase = ackUseCase;
    }

    /// <summary>
    /// Bacen SPI manual §2.2.1 — Entrada: PSP envia mensagens.
    /// Accepts one (application/xml) or up to 10 (multipart/mixed) messages, stores them
    /// for asynchronous processing, and returns 201 with PI-ResourceId(s).
    /// </summary>
    [HttpPost("api/v1/in/{ispb}/msgs")]
    public async Task<IActionResult> PostMessages(string ispb, CancellationToken cancellationToken)
    {
        var contentType = Request.ContentType ?? string.Empty;

        IReadOnlyList<SpiInboundMessage>? messages;
        if (IsSingleXml(contentType))
        {
            using var sr = new StreamReader(Request.Body, Encoding.UTF8);
            var body = await sr.ReadToEndAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body)) return BadRequest();
            messages = new[] { new SpiInboundMessage(body, contentType) };
        }
        else if (IsMultipart(contentType))
        {
            messages = await MultipartRequestReader.ReadAsync(Request, cancellationToken);
            if (messages is null || messages.Count == 0) return BadRequest();
        }
        else
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        var resourceIds = await _receiveUseCase.ExecuteAsync(messages, ispb, cancellationToken);
        Response.Headers["PI-ResourceId"] = string.Join(",", resourceIds);
        return StatusCode(StatusCodes.Status201Created);
    }

    /// <summary>Bacen SPI manual §2.2.2 — start a new outbound stream and long-poll for messages.</summary>
    [HttpGet("api/v1/out/{ispb}/stream/start")]
    public Task<IActionResult> StartStream(string ispb, CancellationToken cancellationToken) =>
        PullAsync(streamId: null, ispb, cancellationToken);

    /// <summary>Bacen SPI manual §2.2.2.2 — continue the stream; the previous batch is implicitly acked.</summary>
    [HttpGet("api/v1/out/{ispb}/stream/{streamId}")]
    public Task<IActionResult> ContinueStream(string ispb, string streamId, CancellationToken cancellationToken) =>
        PullAsync(streamId, ispb, cancellationToken);

    /// <summary>Bacen SPI manual §2.2.2.3 — explicit ack of the last delivered batch.</summary>
    [HttpDelete("api/v1/out/{ispb}/stream/{streamId}")]
    public async Task<IActionResult> AckStream(string ispb, string streamId, CancellationToken cancellationToken)
    {
        var ok = await _ackUseCase.ExecuteAsync(streamId, cancellationToken);
        return ok ? Ok() : StatusCode(StatusCodes.Status410Gone);
    }

    private async Task<IActionResult> PullAsync(string? streamId, string ispb, CancellationToken cancellationToken)
    {
        var multipart = AcceptsMultipart(Request.Headers.Accept.ToString());
        var result = await _pullUseCase.ExecuteAsync(streamId, multipart, ispb, cancellationToken);

        var nextPath = $"/api/v1/out/{ispb}/stream/{result.NextStreamId}";
        Response.Headers["PI-Pull-Next"] = nextPath;

        if (result.Messages.Count == 0)
        {
            return StatusCode(StatusCodes.Status204NoContent);
        }

        Response.Headers["PI-ResourceId"] = string.Join(",", result.Messages.Select(m => m.PiResourceId));
        Response.StatusCode = StatusCodes.Status200OK;

        if (multipart)
        {
            await MultipartResponseWriter.WriteAsync(Response, result.Messages, cancellationToken);
            return new EmptyResult();
        }

        // Single-XML response — only one message in the batch.
        var only = result.Messages[0];
        Response.ContentType = "application/xml; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(only.SignedXml);
        await Response.Body.WriteAsync(bytes, cancellationToken);
        return new EmptyResult();
    }

    private static bool IsSingleXml(string contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return false;
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mt)) return false;
        var mediaType = mt.MediaType.Value;
        return string.Equals(mediaType, "application/xml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "text/xml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMultipart(string contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return false;
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mt)) return false;
        return string.Equals(mt.MediaType.Value, "multipart/mixed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AcceptsMultipart(string acceptHeader)
    {
        if (string.IsNullOrEmpty(acceptHeader)) return false;
        return acceptHeader.Contains("multipart/mixed", StringComparison.OrdinalIgnoreCase);
    }
}
