using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.CorrelateMessages;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;
using Xunit;

namespace ConvivenciaPix.Application.Tests.UseCases.CorrelateMessages;

public sealed class CorrelateSystemAOutboundUseCaseTests
{
    private readonly Mock<ISpiSentMsgRepository> _sentRepoMock = new();
    private readonly Mock<ISpiXmlParser> _xmlParserMock = new();
    private readonly Mock<IKafkaPublisher> _publisherMock = new();
    private readonly Mock<ISpiMetrics> _metricsMock = new();

    private readonly CorrelateSystemAOutboundUseCase _sut;

    private static readonly IReadOnlySet<string> AllowedTypes = new HashSet<string> { "pacs.008" };

    private static readonly string CdcJson = JsonSerializer.Serialize(new
    {
        after = new { MessageId = "MSG-A", XmlMsg = "<pacs008-a/>", Problem = (string?)null }
    });

    public CorrelateSystemAOutboundUseCaseTests()
    {
        _xmlParserMock.Setup(p => p.ExtractMessageType(It.IsAny<string>())).Returns("pacs.008");
        _xmlParserMock.Setup(p => p.ExtractCorrelationKey(It.IsAny<string>(), "pacs.008")).Returns("E2E-1");
        _xmlParserMock.Setup(p => p.GetCorrelationSource("pacs.008")).Returns("MessageKey");
        _xmlParserMock.Setup(p => p.ExtractOriginalIdempotentId(It.IsAny<string>(), "pacs.008"))
            .Returns((string?)null);

        _sut = new CorrelateSystemAOutboundUseCase(
            _sentRepoMock.Object,
            _xmlParserMock.Object,
            _publisherMock.Object,
            _metricsMock.Object,
            NullLogger<CorrelateSystemAOutboundUseCase>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_WhenInserted_RecordsMetric_AndDoesNotPublish()
    {
        SpiSentMsg? passed = null;
        _sentRepoMock.Setup(r => r.UpsertSystemAAsync(It.IsAny<SpiSentMsg>(), It.IsAny<CancellationToken>()))
            .Callback<SpiSentMsg, CancellationToken>((m, _) => passed = m)
            .ReturnsAsync((SpiSentMsg m, CancellationToken _) => new UpsertOutcome<SpiSentMsg>(m, Inserted: true));

        await _sut.ExecuteAsync(CdcJson, AllowedTypes, CancellationToken.None);

        passed.Should().NotBeNull();
        passed!.XmlMsgSystemA.Should().Be("<pacs008-a/>");
        passed.XmlMsgSystemB.Should().BeNull();
        passed.IsComplete.Should().BeFalse();
        _metricsMock.Verify(m => m.RecordCorrelationSource("MessageKey"), Times.Once); // once per row, on insert
        _publisherMock.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<KafkaEnvelope>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenUpdateCompletesRow_PublishesOnce_AndNoMetric()
    {
        var complete = SpiSentMsg.Create("E2E-1", "pacs.008");
        complete.UpdateFromSystemA("MSG-A", "<pacs008-a/>", null);
        complete.UpdateFromSystemB("MSG-B", "<pacs008-b/>", null);
        _sentRepoMock.Setup(r => r.UpsertSystemAAsync(It.IsAny<SpiSentMsg>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpsertOutcome<SpiSentMsg>(complete, Inserted: false));

        await _sut.ExecuteAsync(CdcJson, AllowedTypes, CancellationToken.None);

        _metricsMock.Verify(m => m.RecordCorrelationSource(It.IsAny<string>()), Times.Never); // not on update
        _publisherMock.Verify(p => p.PublishAsync("spi.correlation.events", It.IsAny<KafkaEnvelope>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _publisherMock.Verify(p => p.PublishAsync("spi.comparison.events", It.IsAny<KafkaEnvelope>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Skips_WhenMsgTypeNotAllowed()
    {
        _xmlParserMock.Setup(p => p.ExtractMessageType(It.IsAny<string>())).Returns("pacs.999");

        await _sut.ExecuteAsync(CdcJson, AllowedTypes, CancellationToken.None);

        _sentRepoMock.Verify(r => r.UpsertSystemAAsync(It.IsAny<SpiSentMsg>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
