using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace ConvivenciaPix.Application.UseCases.GeneratePibr002;

public sealed class GeneratePibr002UseCase : IGeneratePibr002UseCase
{
    private const string MsgType = "pibr.001";

    // Matches Topics.SystemBResponses; kept as a literal because the Application layer
    // does not reference Infrastructure (which owns the Topics registry).
    private const string SystemBResponsesTopic = "spi.systemb.responses";

    private readonly ISpiXmlParser _xmlParser;
    private readonly IPibr002Builder _builder;
    private readonly IKafkaPublisher _publisher;
    private readonly ILogger<GeneratePibr002UseCase> _logger;

    public GeneratePibr002UseCase(
        ISpiXmlParser xmlParser,
        IPibr002Builder builder,
        IKafkaPublisher publisher,
        ILogger<GeneratePibr002UseCase> logger)
    {
        _xmlParser = xmlParser;
        _builder = builder;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task ExecuteAsync(KafkaEnvelope envelope, CancellationToken cancellationToken)
    {
        var pibr001Xml = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.PayloadBase64));

        var idempotentId = _xmlParser.ExtractIdempotentId(pibr001Xml, MsgType);
        var pibr002Xml = _builder.Build(pibr001Xml);

        // Reuse the existing sign-and-deliver contract: the proxy worker signs TransformedXml,
        // enqueues it to the outbound stream, and upserts a System-B-only SpiReceivedMsg (which
        // stays IsComplete=false, so no comparison event fires — correct, there is no System A leg).
        var readyDto = new SystemBInboundReadyDto(
            IdempotentId: idempotentId,
            MsgType: "pibr.002",
            TransformedXml: pibr002Xml,
            OccurredAt: DateTimeOffset.UtcNow);

        var outEnvelope = new KafkaEnvelope(
            MessageId: idempotentId,
            PayloadBase64: Convert.ToBase64String(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(readyDto))),
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: idempotentId);

        await _publisher.PublishAsync(SystemBResponsesTopic, outEnvelope, cancellationToken);

        _logger.LogInformation(
            "SPI Echo request answered. Generated pibr.002 for IdempotentId={IdempotentId}",
            idempotentId);
    }
}
