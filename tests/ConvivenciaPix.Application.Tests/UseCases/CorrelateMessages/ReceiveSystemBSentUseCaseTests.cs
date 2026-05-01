using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.CorrelateMessages;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using Xunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;

namespace ConvivenciaPix.Application.Tests.UseCases.CorrelateMessages;

public sealed class ReceiveSystemBSentUseCaseTests
{
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock = new();
    private readonly Mock<IServiceScope> _scopeMock = new();
    private readonly Mock<IServiceProvider> _serviceProviderMock = new();
    private readonly Mock<ISpiPendingSystemBMsgRepository> _repoMock = new();
    private readonly Mock<ISpiXmlParser> _xmlParserMock = new();
    private readonly Mock<ILogger<ReceiveSystemBSentUseCase>> _loggerMock = new();

    private readonly ReceiveSystemBSentUseCase _sut;

    public ReceiveSystemBSentUseCaseTests()
    {
        _scopeFactoryMock.Setup(f => f.CreateScope()).Returns(_scopeMock.Object);
        _scopeMock.Setup(s => s.ServiceProvider).Returns(_serviceProviderMock.Object);
        _serviceProviderMock.Setup(s => s.GetService(typeof(ISpiPendingSystemBMsgRepository)))
            .Returns(_repoMock.Object);

        _sut = new ReceiveSystemBSentUseCase(
            _scopeFactoryMock.Object,
            _xmlParserMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyExists_SkipsStorage()
    {
        var xml = "<xml/>";
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));
        var envelope = new KafkaEnvelope("m1", payload, DateTimeOffset.UtcNow, "sysb-1");

        _repoMock.Setup(r => r.ExistsByIdSystemBAsync("sysb-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.ExecuteAsync(envelope, CancellationToken.None);

        _repoMock.Verify(r => r.AddAsync(It.IsAny<SpiPendingSystemBMsg>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_NewMessage_ParsesAndStores()
    {
        var xml = "<xml/>";
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));
        var envelope = new KafkaEnvelope("m1", payload, DateTimeOffset.UtcNow, "sysb-1");

        _repoMock.Setup(r => r.ExistsByIdSystemBAsync("sysb-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _xmlParserMock.Setup(p => p.ExtractTimestamp(xml)).Returns(DateTimeOffset.UtcNow);
        _xmlParserMock.Setup(p => p.ExtractAmount(xml)).Returns(100.50m);
        _xmlParserMock.Setup(p => p.ExtractPayerId(xml)).Returns("payer");
        _xmlParserMock.Setup(p => p.ExtractPayeeId(xml)).Returns("payee");

        await _sut.ExecuteAsync(envelope, CancellationToken.None);

        _repoMock.Verify(r => r.AddAsync(
            It.Is<SpiPendingSystemBMsg>(m => m.IdSystemB == "sysb-1" && m.Amount == 100.50m),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
