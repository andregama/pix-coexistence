using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.GeneratePibr002;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ConvivenciaPix.Application.Tests.UseCases.GeneratePibr002;

public sealed class GeneratePibr002UseCaseTests
{
    private readonly Mock<ISpiXmlParser> _xmlParserMock = new();
    private readonly Mock<IPibr002Builder> _builderMock = new();
    private readonly Mock<IKafkaPublisher> _publisherMock = new();

    private GeneratePibr002UseCase BuildSut() => new(
        _xmlParserMock.Object,
        _builderMock.Object,
        _publisherMock.Object,
        NullLogger<GeneratePibr002UseCase>.Instance);

    private static KafkaEnvelope EnvelopeWith(string rawXml) => new(
        MessageId: "msg-1",
        PayloadBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes(rawXml)),
        Timestamp: DateTimeOffset.UtcNow);

    [Fact]
    public async Task Execute_PublishesReadyDto_ToResponsesTopic_AsPibr002()
    {
        _xmlParserMock.Setup(p => p.ExtractIdempotentId(It.IsAny<string>(), "pibr.001"))
            .Returns("M99999010abc00000000000000000001");
        _builderMock.Setup(b => b.Build(It.IsAny<string>())).Returns("<pibr002/>");

        KafkaEnvelope? captured = null;
        _publisherMock
            .Setup(p => p.PublishAsync("spi.systemb.responses", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<string, KafkaEnvelope, CancellationToken>((_, env, _) => captured = env);

        await BuildSut().ExecuteAsync(EnvelopeWith("<Envelope/>"), CancellationToken.None);

        _publisherMock.Verify(
            p => p.PublishAsync("spi.systemb.responses", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Once);

        captured.Should().NotBeNull();
        captured!.MessageId.Should().Be("M99999010abc00000000000000000001");
        captured.CorrelationId.Should().Be("M99999010abc00000000000000000001");

        var readyJson = Encoding.UTF8.GetString(Convert.FromBase64String(captured.PayloadBase64));
        var ready = JsonSerializer.Deserialize<SystemBInboundReadyDto>(readyJson)!;
        ready.IdempotentId.Should().Be("M99999010abc00000000000000000001");
        ready.MsgType.Should().Be("pibr.002");
        ready.TransformedXml.Should().Be("<pibr002/>");
    }

    [Fact]
    public async Task Execute_BuildsFromTheDecodedRequestXml()
    {
        _xmlParserMock.Setup(p => p.ExtractIdempotentId(It.IsAny<string>(), "pibr.001")).Returns("id");
        _builderMock.Setup(b => b.Build(It.IsAny<string>())).Returns("<pibr002/>");

        await BuildSut().ExecuteAsync(EnvelopeWith("<Envelope>REQ</Envelope>"), CancellationToken.None);

        _builderMock.Verify(b => b.Build("<Envelope>REQ</Envelope>"), Times.Once);
    }
}
