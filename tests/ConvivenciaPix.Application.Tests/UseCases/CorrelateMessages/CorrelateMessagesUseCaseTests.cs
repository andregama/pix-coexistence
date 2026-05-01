using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.CorrelateMessages;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using ConvivenciaPix.Domain.ValueObjects;
using Xunit;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ConvivenciaPix.Application.Tests.UseCases.CorrelateMessages;

public sealed class CorrelateMessagesUseCaseTests
{
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock = new();
    private readonly Mock<IServiceScope> _scopeMock = new();
    private readonly Mock<IServiceProvider> _serviceProviderMock = new();
    private readonly Mock<IOrchestratorClient> _orchestratorMock = new();
    private readonly Mock<ISpiSentMsgRepository> _sentRepoMock = new();
    private readonly Mock<ISpiPendingSystemBMsgRepository> _pendingRepoMock = new();
    private readonly Mock<ISpiXmlParser> _xmlParserMock = new();
    private readonly Mock<IKafkaPublisher> _publisherMock = new();
    private readonly Mock<ISpiMetrics> _metricsMock = new();
    private readonly Mock<ILogger<CorrelateMessagesUseCase>> _loggerMock = new();
    private readonly IConfiguration _configuration;

    private readonly CorrelateMessagesUseCase _sut;

    public CorrelateMessagesUseCaseTests()
    {
        _scopeFactoryMock.Setup(f => f.CreateScope()).Returns(_scopeMock.Object);
        _scopeMock.Setup(s => s.ServiceProvider).Returns(_serviceProviderMock.Object);
        _serviceProviderMock.Setup(s => s.GetService(typeof(ISpiSentMsgRepository)))
            .Returns(_sentRepoMock.Object);
        _serviceProviderMock.Setup(s => s.GetService(typeof(ISpiPendingSystemBMsgRepository)))
            .Returns(_pendingRepoMock.Object);

        var configMap = new Dictionary<string, string?>
        {
            { "Correlate:HeuristicWindowSeconds", "60" }
        };
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(configMap).Build();

        _sut = new CorrelateMessagesUseCase(
            _scopeFactoryMock.Object,
            _orchestratorMock.Object,
            _xmlParserMock.Object,
            _publisherMock.Object,
            _metricsMock.Object,
            _configuration,
            _loggerMock.Object);
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyCorrelated_Skips()
    {
        var rawCdc = """{"IdSystemA":"A1","MessageId":"m-a","Payload":"<xml/>"}""";
        _sentRepoMock.Setup(r => r.FindByIdSystemAAsync("A1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SpiSentMsg.Create("A1", "B1", CorrelationSource.Orchestrator));

        await _sut.ExecuteAsync(rawCdc, CancellationToken.None);

        _orchestratorMock.Verify(o => o.FindCorrelationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_OrchestratorMatch_SavesAndPublishes()
    {
        var rawCdc = """{"IdSystemA":"A1","MessageId":"m-a","Payload":"<xml/>"}""";
        _sentRepoMock.Setup(r => r.FindByIdSystemAAsync("A1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiSentMsg?)null);
        _orchestratorMock.Setup(o => o.FindCorrelationAsync("A1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrchestratorResult("A1", "B1"));

        await _sut.ExecuteAsync(rawCdc, CancellationToken.None);

        _sentRepoMock.Verify(r => r.AddAsync(
            It.Is<SpiSentMsg>(m => m.IdSystemA == "A1" && m.CorrelationSource == CorrelationSource.Orchestrator),
            It.IsAny<CancellationToken>()), Times.Once);
        _publisherMock.Verify(p => p.PublishAsync("spi.correlation.events", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_HeuristicMatch_SavesAndPublishes()
    {
        var rawCdc = """{"IdSystemA":"A1","MessageId":"m-a","Payload":"<xml/>"}""";
        _sentRepoMock.Setup(r => r.FindByIdSystemAAsync("A1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiSentMsg?)null);
        _orchestratorMock.Setup(o => o.FindCorrelationAsync("A1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrchestratorResult?)null);

        var now = DateTimeOffset.UtcNow;
        _xmlParserMock.Setup(p => p.ExtractTimestamp(It.IsAny<string>())).Returns(now);
        _xmlParserMock.Setup(p => p.ExtractAmount(It.IsAny<string>())).Returns(100m);
        _xmlParserMock.Setup(p => p.ExtractPayerId(It.IsAny<string>())).Returns("payer");
        _xmlParserMock.Setup(p => p.ExtractPayeeId(It.IsAny<string>())).Returns("payee");

        var match = SpiPendingSystemBMsg.Create("B1", "m1", now, 100m, "payer", "payee", "<xml/>");
        _pendingRepoMock.Setup(r => r.FindHeuristicMatchAsync(now, 100m, "payer", "payee", 60, It.IsAny<CancellationToken>()))
            .ReturnsAsync(match);

        await _sut.ExecuteAsync(rawCdc, CancellationToken.None);

        _sentRepoMock.Verify(r => r.AddAsync(
            It.Is<SpiSentMsg>(m => m.IdSystemA == "A1" && m.CorrelationSource == CorrelationSource.Heuristic),
            It.IsAny<CancellationToken>()), Times.Once);
        _pendingRepoMock.Verify(r => r.DeleteAsync(match.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NoMatch_ThrowsInvalidOperationException()
    {
        var rawCdc = """{"IdSystemA":"A1","MessageId":"m-a","Payload":"<xml/>"}""";
        _sentRepoMock.Setup(r => r.FindByIdSystemAAsync("A1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiSentMsg?)null);
        _orchestratorMock.Setup(o => o.FindCorrelationAsync("A1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrchestratorResult?)null);
        _pendingRepoMock.Setup(r => r.FindHeuristicMatchAsync(It.IsAny<DateTimeOffset>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiPendingSystemBMsg?)null);

        var act = () => _sut.ExecuteAsync(rawCdc, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
