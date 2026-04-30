using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Infrastructure.Messaging;
using ConvivenciaPix.SpiProxyApi.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text;
using System.Xml.Linq;

namespace ConvivenciaPix.SpiProxyApi.Controllers;

[ApiController]
[Route("api/spi")]
public sealed class SpiController : ControllerBase
{
    private readonly IResponseCache _cache;
    private readonly IKafkaPublisher _publisher;
    private readonly ISpiXmlParser _xmlParser;
    private readonly ProxyApiOptions _options;
    private readonly ILogger<SpiController> _logger;

    public SpiController(
        IResponseCache cache,
        IKafkaPublisher publisher,
        ISpiXmlParser xmlParser,
        IOptions<ProxyApiOptions> options,
        ILogger<SpiController> logger)
    {
        _cache = cache;
        _publisher = publisher;
        _xmlParser = xmlParser;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Simulates the Bacen SPI endpoint for System B. Accepts a raw SPI XML message,
    /// publishes it to Kafka, and long-polls Redis until the proxy worker deposits the
    /// signed System A response — or returns a Bacen-compliant timeout error after the
    /// configured timeout.
    /// </summary>
    [HttpPost("messages")]
    [Consumes("application/xml", "text/xml")]
    public async Task<IActionResult> PostMessage(CancellationToken cancellationToken)
    {
        string rawXml;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            rawXml = await reader.ReadToEndAsync(cancellationToken);

        string messageId;
        string idSystemB;
        try
        {
            messageId = _xmlParser.ExtractMessageId(rawXml);
            idSystemB = _xmlParser.ExtractEndToEndId(rawXml);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogInvalidXml(ex.Message);
            return Content(BuildErrorXml("RJCT", "SPI2023", "Invalid SPI message format"), "application/xml");
        }

        // Idempotency: return cached response immediately if already processed
        var cached = await _cache.GetIdempotencyKeyAsync(messageId, cancellationToken);
        if (cached is not null)
        {
            _logger.LogIdempotentHit(messageId);
            return Content(cached, "application/xml");
        }

        // Store raw XML so the proxy worker can include it in the comparison event
        await _cache.SetAsync(
            $"request:{idSystemB}", rawXml,
            TimeSpan.FromHours(1), cancellationToken);

        var receivedAt = DateTimeOffset.UtcNow;
        var envelope = new KafkaEnvelope(
            MessageId: messageId,
            PayloadBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes(rawXml)),
            Timestamp: receivedAt,
            CorrelationId: idSystemB);

        await _publisher.PublishAsync(Topics.SystemBRequests, envelope, cancellationToken);
        _logger.LogRequestPublished(messageId, idSystemB);

        // Poll Redis for the response deposited by the proxy worker
        var timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        var deadline = receivedAt + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await _cache.GetAsync(idSystemB, cancellationToken);
            if (response is not null)
            {
                await _cache.SetIdempotencyKeyAsync(messageId, response, TimeSpan.FromHours(24), cancellationToken);
                _logger.LogResponseReturned(messageId, idSystemB);
                return Content(response, "application/xml");
            }

            await Task.Delay(500, cancellationToken);
        }

        _logger.LogRequestTimeout(messageId, idSystemB, _options.TimeoutSeconds);
        return StatusCode(504, Content(BuildErrorXml("RJCT", "SPI9999", "Processing timeout"), "application/xml"));
    }

    private static string BuildErrorXml(string transactionStatus, string reasonCode, string additionalInfo) =>
        new XDocument(
            new XElement("Document",
                new XElement("FIToFIPmtStsRpt",
                    new XElement("GrpHdr",
                        new XElement("MsgId", Guid.NewGuid().ToString()),
                        new XElement("CreDtTm", DateTimeOffset.UtcNow.ToString("O"))),
                    new XElement("TxInfAndSts",
                        new XElement("TxSts", transactionStatus),
                        new XElement("StsRsnInf",
                            new XElement("Rsn",
                                new XElement("Cd", reasonCode)),
                            new XElement("AddtlInf", additionalInfo)))))).ToString();
}

internal static partial class SpiControllerLogMessages
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid SPI XML received: {Error}")]
    public static partial void LogInvalidXml(this ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Idempotent hit for MessageId={MessageId} — returning cached response")]
    public static partial void LogIdempotentHit(this ILogger logger, string messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "SPI request published. MessageId={MessageId} IdSystemB={IdSystemB}")]
    public static partial void LogRequestPublished(this ILogger logger, string messageId, string idSystemB);

    [LoggerMessage(Level = LogLevel.Information, Message = "SPI response returned. MessageId={MessageId} IdSystemB={IdSystemB}")]
    public static partial void LogResponseReturned(this ILogger logger, string messageId, string idSystemB);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SPI request timed out after {TimeoutSeconds}s. MessageId={MessageId} IdSystemB={IdSystemB}")]
    public static partial void LogRequestTimeout(this ILogger logger, string messageId, string idSystemB, int timeoutSeconds);
}
