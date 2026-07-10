using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.ReceiveSpiRequest;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ConvivenciaPix.Application.Tests.UseCases.ReceiveSpiRequest;

public sealed class ReceiveSpiRequestUseCaseTests
{
    private readonly Mock<IKafkaPublisher> _publisherMock = new();
    private readonly Mock<ISpiXmlParser> _xmlParserMock = new();
    private readonly Mock<IResponseCache> _cacheMock = new();

    private ReceiveSpiRequestUseCase BuildSut() => new(
        _publisherMock.Object,
        _xmlParserMock.Object,
        _cacheMock.Object,
        NullLogger<ReceiveSpiRequestUseCase>.Instance);

    [Fact]
    public async Task SingleMessage_ReturnsOnePiResourceId_PublishesEnvelopeWithIspb()
    {
        _xmlParserMock.Setup(p => p.ExtractMessageId(It.IsAny<string>())).Returns("msg-1");
        _xmlParserMock.Setup(p => p.ExtractMessageType(It.IsAny<string>())).Returns("pacs.008");
        _xmlParserMock.Setup(p => p.ExtractIdempotentId(It.IsAny<string>(), It.IsAny<string>())).Returns("e2e-1");
        KafkaEnvelope? captured = null;
        _publisherMock
            .Setup(p => p.PublishAsync("spi.systemb.requests", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<string, KafkaEnvelope, CancellationToken>((_, env, _) => captured = env);

        var sut = BuildSut();
        var ids = await sut.ExecuteAsync(
            new[] { new SpiInboundMessage("<Document/>", "application/xml") },
            ispb: "12345678",
            CancellationToken.None);

        ids.Should().HaveCount(1);
        ids[0].Should().NotBeNullOrWhiteSpace();
        captured.Should().NotBeNull();
        captured!.Ispb.Should().Be("12345678");
        captured.PiResourceId.Should().Be(ids[0]);
        captured.MessageId.Should().Be("msg-1");
        captured.CorrelationId.Should().Be("e2e-1");
    }

    [Fact]
    public async Task MultipleMessages_ReturnsResourceIdsInOrder_PublishesEach()
    {
        _xmlParserMock.Setup(p => p.ExtractMessageId(It.IsAny<string>())).Returns("msg");
        _xmlParserMock.Setup(p => p.ExtractMessageType(It.IsAny<string>())).Returns("pacs.008");
        _xmlParserMock.Setup(p => p.ExtractIdempotentId(It.IsAny<string>(), It.IsAny<string>())).Returns("e2e");

        var sut = BuildSut();
        var inbound = new[]
        {
            new SpiInboundMessage("<a/>", "application/xml"),
            new SpiInboundMessage("<b/>", "application/xml"),
            new SpiInboundMessage("<c/>", "application/xml"),
        };
        var ids = await sut.ExecuteAsync(inbound, ispb: "11111111", CancellationToken.None);

        ids.Should().HaveCount(3);
        ids.Distinct().Should().HaveCount(3);
        _publisherMock.Verify(
            p => p.PublishAsync("spi.systemb.requests", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task UnparseableXml_StillAcksWithFreshPiResourceId()
    {
        // Per Bacen §2.2.1: no syntactic/signature validation at this layer.
        _xmlParserMock.Setup(p => p.ExtractMessageId(It.IsAny<string>()))
            .Throws(new InvalidOperationException("not valid"));

        var sut = BuildSut();
        var ids = await sut.ExecuteAsync(
            new[] { new SpiInboundMessage("not xml", "application/xml") },
            ispb: "00000000",
            CancellationToken.None);

        ids.Should().HaveCount(1);
        _publisherMock.Verify(
            p => p.PublishAsync("spi.systemb.requests", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DuplicateMsgId_BothSucceedWithDistinctPiResourceIds()
    {
        // Per Bacen §2.2.1: duplicate sends are GRAVADAS de novo; idempotency happens during processing.
        _xmlParserMock.Setup(p => p.ExtractMessageId(It.IsAny<string>())).Returns("same-msg");
        _xmlParserMock.Setup(p => p.ExtractMessageType(It.IsAny<string>())).Returns("pacs.008");
        _xmlParserMock.Setup(p => p.ExtractIdempotentId(It.IsAny<string>(), It.IsAny<string>())).Returns("same-e2e");

        var sut = BuildSut();
        var first = await sut.ExecuteAsync(
            new[] { new SpiInboundMessage("<a/>", "application/xml") }, "11111111", CancellationToken.None);
        var second = await sut.ExecuteAsync(
            new[] { new SpiInboundMessage("<a/>", "application/xml") }, "11111111", CancellationToken.None);

        first[0].Should().NotBe(second[0]);
        _publisherMock.Verify(
            p => p.PublishAsync("spi.systemb.requests", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task Pacs004_CorrelatesOnRtrIdempotentId_AndCachesRawXml()
    {
        // Regression guard: pacs.004 has no bare <EndToEndId>; its idempotency key is TxInf/RtrId.
        // The old ExtractEndToEndId path threw here, leaving CorrelationId and the request cache null.
        _xmlParserMock.Setup(p => p.ExtractMessageId(It.IsAny<string>())).Returns("msg-rtr");
        _xmlParserMock.Setup(p => p.ExtractMessageType(It.IsAny<string>())).Returns("pacs.004");
        _xmlParserMock.Setup(p => p.ExtractIdempotentId(It.IsAny<string>(), "pacs.004")).Returns("RTR-123");
        KafkaEnvelope? captured = null;
        _publisherMock
            .Setup(p => p.PublishAsync("spi.systemb.requests", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<string, KafkaEnvelope, CancellationToken>((_, env, _) => captured = env);

        var sut = BuildSut();
        await sut.ExecuteAsync(
            new[] { new SpiInboundMessage("<PmtRtr/>", "application/xml") },
            ispb: "12345678",
            CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.CorrelationId.Should().Be("RTR-123");
        _cacheMock.Verify(
            c => c.SetAsync("request:RTR-123", It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task IdempotentIdExtractionThrows_StillAcksWithFreshPiResourceId()
    {
        // The message type is resolvable but the idempotency field is missing; best-effort
        // extraction must still enqueue the message (Bacen §2.2.1 — no validation at this layer).
        _xmlParserMock.Setup(p => p.ExtractMessageId(It.IsAny<string>())).Returns("msg-x");
        _xmlParserMock.Setup(p => p.ExtractMessageType(It.IsAny<string>())).Returns("pacs.008");
        _xmlParserMock.Setup(p => p.ExtractIdempotentId(It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("missing EndToEndId"));

        var sut = BuildSut();
        var ids = await sut.ExecuteAsync(
            new[] { new SpiInboundMessage("<Document/>", "application/xml") },
            ispb: "00000000",
            CancellationToken.None);

        ids.Should().HaveCount(1);
        _publisherMock.Verify(
            p => p.PublishAsync("spi.systemb.requests", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
