using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.CorrelateMessages;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text;
using Xunit;

namespace ConvivenciaPix.Application.Tests.UseCases.CorrelateMessages;

public sealed class CorrelateSystemBOutboundUseCaseTests
{
    private readonly Mock<ISpiSentMsgRepository> _sentRepoMock = new();
    private readonly Mock<ISpiXmlParser> _xmlParserMock = new();
    private readonly Mock<IKafkaPublisher> _publisherMock = new();
    private readonly Mock<ISpiMetrics> _metricsMock = new();

    private readonly CorrelateSystemBOutboundUseCase _sut;

    private static readonly IReadOnlySet<string> AllowedTypes = new HashSet<string> { "pacs.008" };

    private static readonly KafkaEnvelope Envelope = new(
        MessageId: "MSG-B",
        PayloadBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes("<pacs008-b/>")),
        Timestamp: DateTimeOffset.UtcNow,
        CorrelationId: "E2E-1");

    public CorrelateSystemBOutboundUseCaseTests()
    {
        _xmlParserMock.Setup(p => p.ExtractMessageType(It.IsAny<string>())).Returns("pacs.008");
        _xmlParserMock.Setup(p => p.ExtractCorrelationKey(It.IsAny<string>(), "pacs.008")).Returns("E2E-1");
        _xmlParserMock.Setup(p => p.GetCorrelationSource("pacs.008")).Returns("MessageKey");
        _xmlParserMock.Setup(p => p.ExtractOriginalIdempotentId(It.IsAny<string>(), "pacs.008"))
            .Returns((string?)null);
        _xmlParserMock.Setup(p => p.ExtractMessageId(It.IsAny<string>())).Returns("MSG-B");

        _sut = new CorrelateSystemBOutboundUseCase(
            _sentRepoMock.Object,
            _xmlParserMock.Object,
            _publisherMock.Object,
            _metricsMock.Object,
            NullLogger<CorrelateSystemBOutboundUseCase>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRowAbsent_CreatesRow_AndDoesNotPublish()
    {
        _sentRepoMock.Setup(r => r.FindByIdempotentIdAsync("E2E-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiSentMsg?)null);

        SpiSentMsg? added = null;
        _sentRepoMock.Setup(r => r.AddAsync(It.IsAny<SpiSentMsg>(), It.IsAny<CancellationToken>()))
            .Callback<SpiSentMsg, CancellationToken>((m, _) => added = m)
            .Returns(Task.CompletedTask);

        await _sut.ExecuteAsync(Envelope, AllowedTypes, CancellationToken.None);

        _sentRepoMock.Verify(r => r.AddAsync(It.IsAny<SpiSentMsg>(), It.IsAny<CancellationToken>()), Times.Once);
        _sentRepoMock.Verify(r => r.UpdateAsync(It.IsAny<SpiSentMsg>(), It.IsAny<CancellationToken>()), Times.Never);

        added.Should().NotBeNull();
        added!.XmlMsgSystemB.Should().Be("<pacs008-b/>");
        added.XmlMsgSystemA.Should().BeNull();
        added.IsComplete.Should().BeFalse();

        _publisherMock.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<KafkaEnvelope>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCounterpartPresent_UpdatesRow_AndPublishesOnComplete()
    {
        var existing = SpiSentMsg.Create("E2E-1", "pacs.008");
        existing.UpdateFromSystemA("MSG-A", "<pacs008-a/>", null);
        _sentRepoMock.Setup(r => r.FindByIdempotentIdAsync("E2E-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        await _sut.ExecuteAsync(Envelope, AllowedTypes, CancellationToken.None);

        _sentRepoMock.Verify(r => r.UpdateAsync(existing, It.IsAny<CancellationToken>()), Times.Once);
        _sentRepoMock.Verify(r => r.AddAsync(It.IsAny<SpiSentMsg>(), It.IsAny<CancellationToken>()), Times.Never);
        existing.IsComplete.Should().BeTrue();

        _publisherMock.Verify(p => p.PublishAsync("spi.correlation.events", It.IsAny<KafkaEnvelope>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _publisherMock.Verify(p => p.PublishAsync("spi.comparison.events", It.IsAny<KafkaEnvelope>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Skips_WhenMsgTypeNotAllowed()
    {
        _xmlParserMock.Setup(p => p.ExtractMessageType(It.IsAny<string>())).Returns("pacs.999");

        await _sut.ExecuteAsync(Envelope, AllowedTypes, CancellationToken.None);

        _sentRepoMock.Verify(r => r.AddAsync(It.IsAny<SpiSentMsg>(), It.IsAny<CancellationToken>()), Times.Never);
        _sentRepoMock.Verify(r => r.UpdateAsync(It.IsAny<SpiSentMsg>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
