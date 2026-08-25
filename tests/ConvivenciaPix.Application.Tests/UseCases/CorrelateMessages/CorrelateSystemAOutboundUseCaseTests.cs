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
    public async Task ExecuteAsync_WhenRowAbsent_CreatesRow_AndDoesNotPublish()
    {
        _sentRepoMock.Setup(r => r.FindByIdempotentIdAsync("E2E-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiSentMsg?)null);

        SpiSentMsg? added = null;
        _sentRepoMock.Setup(r => r.AddAsync(It.IsAny<SpiSentMsg>(), It.IsAny<CancellationToken>()))
            .Callback<SpiSentMsg, CancellationToken>((m, _) => added = m)
            .Returns(Task.CompletedTask);

        await _sut.ExecuteAsync(CdcJson, AllowedTypes, CancellationToken.None);

        _sentRepoMock.Verify(r => r.AddAsync(It.IsAny<SpiSentMsg>(), It.IsAny<CancellationToken>()), Times.Once);
        _sentRepoMock.Verify(r => r.UpdateAsync(It.IsAny<SpiSentMsg>(), It.IsAny<CancellationToken>()), Times.Never);

        added.Should().NotBeNull();
        added!.XmlMsgSystemA.Should().Be("<pacs008-a/>");
        added.XmlMsgSystemB.Should().BeNull();
        added.IsComplete.Should().BeFalse();

        _publisherMock.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<KafkaEnvelope>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCounterpartPresent_UpdatesRow_AndPublishesOnComplete()
    {
        var existing = SpiSentMsg.Create("E2E-1", "pacs.008");
        existing.UpdateFromSystemB("MSG-B", "<pacs008-b/>", null);
        _sentRepoMock.Setup(r => r.FindByIdempotentIdAsync("E2E-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        await _sut.ExecuteAsync(CdcJson, AllowedTypes, CancellationToken.None);

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

        await _sut.ExecuteAsync(CdcJson, AllowedTypes, CancellationToken.None);

        _sentRepoMock.Verify(r => r.AddAsync(It.IsAny<SpiSentMsg>(), It.IsAny<CancellationToken>()), Times.Never);
        _sentRepoMock.Verify(r => r.UpdateAsync(It.IsAny<SpiSentMsg>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
