using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace ConvivenciaPix.Application.UseCases.ReceiveSpiRequest;

public sealed class ReceiveSpiRequestUseCase : IReceiveSpiRequestUseCase
{
    private readonly IResponseCache _cache;
    private readonly IKafkaPublisher _publisher;
    private readonly ISpiXmlParser _xmlParser;
    private readonly IValidator<string> _validator;
    private readonly ILogger<ReceiveSpiRequestUseCase> _logger;
    private readonly int _timeoutSeconds;

    public ReceiveSpiRequestUseCase(
        IResponseCache cache,
        IKafkaPublisher publisher,
        ISpiXmlParser xmlParser,
        IValidator<string> validator,
        IOptions<SpiProxyOptions> options,
        ILogger<ReceiveSpiRequestUseCase> logger)
    {
        _cache = cache;
        _publisher = publisher;
        _xmlParser = xmlParser;
        _validator = validator;
        _logger = logger;
        _timeoutSeconds = options.Value.TimeoutSeconds;
    }

    public async Task<ReceiveSpiRequestResult> ExecuteAsync(string rawXml, CancellationToken cancellationToken)
    {
        var validationResult = await _validator.ValidateAsync(rawXml, cancellationToken);
        if (!validationResult.IsValid)
        {
            var error = validationResult.Errors.First().ErrorMessage;
            _logger.LogWarning("SPI XML validation failed: {Error}", error);
            return new ReceiveSpiRequestResult(IsError: true, ErrorCode: "SPI2023", ErrorReason: error);
        }

        string messageId;
        string idSystemB;
        try
        {
            messageId = _xmlParser.ExtractMessageId(rawXml);
            idSystemB = _xmlParser.ExtractEndToEndId(rawXml);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Invalid SPI XML received: {Error}", ex.Message);
            return new ReceiveSpiRequestResult(IsError: true, ErrorCode: "SPI2023", ErrorReason: "Invalid SPI message format");
        }

        // Idempotency: return cached response immediately if already processed
        var cached = await _cache.GetIdempotencyKeyAsync(messageId, cancellationToken);
        if (cached is not null)
        {
            _logger.LogDebug("Idempotent hit for MessageId={MessageId} — returning cached response", messageId);
            return new ReceiveSpiRequestResult(ResponseXml: cached);
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

        // Topic names should ideally be moved to Application Constants or a Shared project
        // For now, I'll use the literal string or assume I can get it from somewhere
        // In the original controller it used Topics.SystemBRequests
        await _publisher.PublishAsync("spi.systemb.requests", envelope, cancellationToken);
        _logger.LogInformation("SPI request published. MessageId={MessageId} IdSystemB={IdSystemB}", messageId, idSystemB);

        // Wait for the response deposited by the proxy worker via Redis Pub/Sub signaling
        var timeout = TimeSpan.FromSeconds(_timeoutSeconds);
        var response = await _cache.WaitForResponseAsync(idSystemB, timeout, cancellationToken);

        if (response is not null)
        {
            await _cache.SetIdempotencyKeyAsync(messageId, response, TimeSpan.FromHours(24), cancellationToken);
            _logger.LogInformation("SPI response returned. MessageId={MessageId} IdSystemB={IdSystemB}", messageId, idSystemB);
            return new ReceiveSpiRequestResult(ResponseXml: response);
        }

        _logger.LogWarning("SPI request timed out after {TimeoutSeconds}s. MessageId={MessageId} IdSystemB={IdSystemB}", messageId, idSystemB, _timeoutSeconds);
        return new ReceiveSpiRequestResult(IsError: true, ErrorCode: "SPI9999", ErrorReason: "Processing timeout");
    }
}

public sealed class SpiProxyOptions
{
    public int TimeoutSeconds { get; set; } = 30;
}
