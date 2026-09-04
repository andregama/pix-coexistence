using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.PropagateResponse;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ConvivenciaPix.Application.Tests.UseCases.PropagateResponse;

public sealed class PropagateResponseUseCaseTests
{
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock = new();
    private readonly Mock<IServiceScope> _scopeMock = new();
    private readonly Mock<IServiceProvider> _serviceProviderMock = new();
    private readonly Mock<IOutboundStream> _outboundMock = new();
    private readonly Mock<IHsmService> _hsmMock = new();
    private readonly Mock<IKafkaPublisher> _publisherMock = new();
    private readonly Mock<ISpiMetrics> _metricsMock = new();
    private readonly Mock<ISpiReceivedMsgRepository> _receivedRepoMock = new();

    private readonly PropagateResponseUseCase _sut;

    private static readonly SystemBInboundReadyDto Ready = new(
        IdempotentId: "E123456789",
        MsgType: "pacs.002",
        TransformedXml: "<pacs/>",
        OccurredAt: DateTimeOffset.UtcNow);

    public PropagateResponseUseCaseTests()
    {
        // CreateAsyncScope() is an extension method that calls CreateScope() internally
        _scopeFactoryMock.Setup(f => f.CreateScope()).Returns(_scopeMock.Object);
        _scopeMock.Setup(s => s.ServiceProvider).Returns(_serviceProviderMock.Object);
        _serviceProviderMock
            .Setup(s => s.GetService(typeof(ISpiReceivedMsgRepository)))
            .Returns(_receivedRepoMock.Object);

        _sut = new PropagateResponseUseCase(
            _scopeFactoryMock.Object,
            _outboundMock.Object,
            _hsmMock.Object,
            _publisherMock.Object,
            _metricsMock.Object,
            NullLogger<PropagateResponseUseCase>.Instance);
    }

    // The B-side upsert returns the passed (not-yet-complete) entity, unless a test overrides it.
    private void SetupUpsertReturnsPassed() =>
        _receivedRepoMock.Setup(r => r.UpsertSystemBAsync(It.IsAny<SpiReceivedMsg>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiReceivedMsg m, CancellationToken _) => new UpsertOutcome<SpiReceivedMsg>(m, Inserted: true));

    [Fact]
    public async Task ExecuteAsync_SignsAndEnqueuesTransformedXml()
    {
        SetupCert("<signed/>");
        SetupUpsertReturnsPassed();

        await _sut.ExecuteAsync(Ready, CancellationToken.None);

        _outboundMock.Verify(
            o => o.EnqueueAsync(It.IsAny<string>(), "<signed/>", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenComplete_PublishesComparisonEvent()
    {
        SetupCert("<signed/>");

        // The upsert returns the merged, complete row (A already present + B just written).
        var complete = SpiReceivedMsg.CreateFromSystemA("E123456789", "pacs.002", null, "<xmlA/>", null);
        complete.SetSystemBXml("<signed/>");
        _receivedRepoMock.Setup(r => r.UpsertSystemBAsync(It.IsAny<SpiReceivedMsg>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpsertOutcome<SpiReceivedMsg>(complete, Inserted: false));

        await _sut.ExecuteAsync(Ready, CancellationToken.None);

        _publisherMock.Verify(
            p => p.PublishAsync("spi.comparison.events", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNotComplete_DoesNotPublishComparisonEvent()
    {
        SetupCert("<signed/>");
        SetupUpsertReturnsPassed(); // returns a B-only row → not complete

        await _sut.ExecuteAsync(Ready, CancellationToken.None);

        _publisherMock.Verify(
            p => p.PublishAsync("spi.comparison.events", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void SetupCert(string signedXml) =>
        _hsmMock.Setup(h => h.SignXmlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(signedXml);
}
