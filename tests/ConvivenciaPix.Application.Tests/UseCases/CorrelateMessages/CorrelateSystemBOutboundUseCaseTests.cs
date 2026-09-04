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
    public async Task ExecuteAsync_WhenInserted_RecordsMetric_AndDoesNotPublish()
    {
        SpiSentMsg? passed = null;
        _sentRepoMock.Setup(r => r.UpsertSystemBAsync(It.IsAny<SpiSentMsg>(), It.IsAny<CancellationToken>()))
            .Callback<SpiSentMsg, CancellationToken>((m, _) => passed = m)
            .ReturnsAsync((SpiSentMsg m, CancellationToken _) => new UpsertOutcome<SpiSentMsg>(m, Inserted: true));

        await _sut.ExecuteAsync(Envelope, AllowedTypes, CancellationToken.None);

        passed.Should().NotBeNull();
        passed!.XmlMsgSystemB.Should().Be("<pacs008-b/>");
        passed.XmlMsgSystemA.Should().BeNull();
        passed.IsComplete.Should().BeFalse();
        _metricsMock.Verify(m => m.RecordCorrelationSource("MessageKey"), Times.Once);
        _publisherMock.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<KafkaEnvelope>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenUpdateCompletesRow_PublishesOnce_AndNoMetric()
    {
        var complete = SpiSentMsg.Create("E2E-1", "pacs.008");
        complete.UpdateFromSystemA("MSG-A", "<pacs008-a/>", null);
        complete.UpdateFromSystemB("MSG-B", "<pacs008-b/>", null);
        _sentRepoMock.Setup(r => r.UpsertSystemBAsync(It.IsAny<SpiSentMsg>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpsertOutcome<SpiSentMsg>(complete, Inserted: false));

        await _sut.ExecuteAsync(Envelope, AllowedTypes, CancellationToken.None);

        _metricsMock.Verify(m => m.RecordCorrelationSource(It.IsAny<string>()), Times.Never);
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

        _sentRepoMock.Verify(r => r.UpsertSystemBAsync(It.IsAny<SpiSentMsg>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
