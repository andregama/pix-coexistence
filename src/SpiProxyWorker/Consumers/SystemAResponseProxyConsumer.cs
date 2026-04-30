using Confluent.Kafka;
using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Domain.Repositories;
using ConvivenciaPix.Infrastructure.Messaging;
using ConvivenciaPix.Infrastructure.Messaging.Debezium;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ConvivenciaPix.SpiProxyWorker.Consumers;

/// <summary>
/// Consumes System A CDC responses, looks up the correlated System B ID, signs the XML,
/// deposits the response into Redis (unblocking the API polling), and publishes a
/// comparison event so the comparison engine can validate System B accuracy.
/// </summary>
public sealed class SystemAResponseProxyConsumer : KafkaConsumerBase<string, string>
{
    private const int CorrelationRetries = 5;
    private static readonly TimeSpan CorrelationRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ResponseCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RequestCacheTtl = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IResponseCache _responseCache;
    private readonly IHsmService _hsmService;
    private readonly IXmlSigningService _xmlSigningService;
    private readonly IKafkaPublisher _publisher;
    private readonly ILogger<SystemAResponseProxyConsumer> _logger;

    public SystemAResponseProxyConsumer(
        IConfiguration configuration,
        IProducer<string, string> dlqProducer,
        IServiceScopeFactory scopeFactory,
        IResponseCache responseCache,
        IHsmService hsmService,
        IXmlSigningService xmlSigningService,
        IKafkaPublisher publisher,
        ILogger<SystemAResponseProxyConsumer> logger)
        : base(BuildConsumer(configuration), dlqProducer, Topics.SystemAResponses, logger)
    {
        _scopeFactory = scopeFactory;
        _responseCache = responseCache;
        _hsmService = hsmService;
        _xmlSigningService = xmlSigningService;
        _publisher = publisher;
        _logger = logger;
    }

    protected override async Task ProcessMessageAsync(
        ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        var responseDto = SystemAOutboxMapper.MapV1(result.Message.Value);
        var idSystemA = responseDto.IdSystemA;

        // Retry loop to handle the race between correlate worker and proxy worker
        string? idSystemB = null;
        for (var attempt = 1; attempt <= CorrelationRetries; attempt++)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISpiSentMsgRepository>();
            var sentMsg = await repo.FindByIdSystemAAsync(idSystemA, cancellationToken);

            if (sentMsg is not null)
            {
                idSystemB = sentMsg.IdSystemB;
                break;
            }

            if (attempt < CorrelationRetries)
            {
                _logger.LogCorrelationRetry(idSystemA, attempt, CorrelationRetries);
                await Task.Delay(CorrelationRetryDelay, cancellationToken);
            }
        }

        if (idSystemB is null)
            throw new InvalidOperationException(
                $"Correlation not found for IdSystemA={idSystemA} after {CorrelationRetries} retries. Routing to DLQ.");

        // Retrieve System B's original request for the comparison event
        var systemBXml = await _responseCache.GetAsync($"request:{idSystemB}", cancellationToken);
        if (systemBXml is null)
            _logger.LogSystemBRequestMissing(idSystemB);

        // Sign System A's XML response
        var cert = await _hsmService.GetSigningCertificateAsync(cancellationToken);
        var signedXml = await _xmlSigningService.SignAsync(
            XDocument.Parse(responseDto.SignedXml), cert);

        // Deposit signed response in Redis — unblocks the API's polling loop
        await _responseCache.SetAsync(idSystemB, signedXml, ResponseCacheTtl, cancellationToken);
        _logger.LogResponseDeposited(idSystemA, idSystemB);

        // Publish signed response to System B responses topic
        var responseEnvelope = new KafkaEnvelope(
            MessageId: responseDto.MessageId,
            PayloadBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes(signedXml)),
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: idSystemB);

        await _publisher.PublishAsync(Topics.SystemBResponses, responseEnvelope, cancellationToken);

        // Publish comparison event for the comparison engine
        if (systemBXml is not null)
        {
            await PublishComparisonEventAsync(
                idSystemA, idSystemB, responseDto.SignedXml, systemBXml, cancellationToken);
        }
    }

    private async Task PublishComparisonEventAsync(
        string idSystemA, string idSystemB,
        string systemAXml, string systemBXml,
        CancellationToken cancellationToken)
    {
        var comparisonEvent = new SpiComparisonEventDto(
            IdSystemA: idSystemA,
            IdSystemB: idSystemB,
            SystemAXml: systemAXml,
            SystemBXml: systemBXml,
            CorrelationSource: "Unknown", // actual source is in the correlation DB; engine doesn't need it
            OccurredAt: DateTimeOffset.UtcNow);

        var envelope = new KafkaEnvelope(
            MessageId: Guid.NewGuid().ToString(),
            PayloadBase64: Convert.ToBase64String(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(comparisonEvent))),
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: idSystemA);

        await _publisher.PublishAsync(Topics.ComparisonEvents, envelope, cancellationToken);
    }

    private static IConsumer<string, string> BuildConsumer(IConfiguration configuration) =>
        new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = configuration["Kafka:BootstrapServers"]
                ?? throw new InvalidOperationException("Kafka:BootstrapServers is required."),
            GroupId = "spi-proxy-systema",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();
}

internal static partial class SystemAResponseProxyConsumerLogMessages
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Correlation not yet available for IdSystemA={IdSystemA} (attempt {Attempt}/{Max}) — retrying")]
    public static partial void LogCorrelationRetry(this ILogger logger, string idSystemA, int attempt, int max);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "System B request XML not found in Redis for IdSystemB={IdSystemB} — comparison event will be skipped")]
    public static partial void LogSystemBRequestMissing(this ILogger logger, string idSystemB);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Signed response deposited in Redis. IdSystemA={IdSystemA} → IdSystemB={IdSystemB}")]
    public static partial void LogResponseDeposited(this ILogger logger, string idSystemA, string idSystemB);
}
