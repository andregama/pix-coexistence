using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.PropagateResponse;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using ConvivenciaPix.Domain.ValueObjects;
using Xunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

namespace ConvivenciaPix.Application.Tests.UseCases.PropagateResponse;

public sealed class PropagateResponseUseCaseTests
{
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock = new();
    private readonly Mock<IServiceScope> _scopeMock = new();
    private readonly Mock<IServiceProvider> _serviceProviderMock = new();
    private readonly Mock<IResponseCache> _cacheMock = new();
    private readonly Mock<IHsmService> _hsmMock = new();
    private readonly Mock<IXmlSigningService> _signingMock = new();
    private readonly Mock<IKafkaPublisher> _publisherMock = new();
    private readonly Mock<ISpiMetrics> _metricsMock = new();
    private readonly Mock<ISpiSentMsgRepository> _sentRepoMock = new();
    private readonly Mock<ILogger<PropagateResponseUseCase>> _loggerMock = new();

    private readonly PropagateResponseUseCase _sut;

    public PropagateResponseUseCaseTests()
    {
        _scopeFactoryMock.Setup(f => f.CreateScope()).Returns(_scopeMock.Object);
        _scopeMock.Setup(s => s.ServiceProvider).Returns(_serviceProviderMock.Object);
        _serviceProviderMock.Setup(s => s.GetService(typeof(ISpiSentMsgRepository)))
            .Returns(_sentRepoMock.Object);

        _sut = new PropagateResponseUseCase(
            _scopeFactoryMock.Object,
            _cacheMock.Object,
            _hsmMock.Object,
            _signingMock.Object,
            _publisherMock.Object,
            _metricsMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task ExecuteAsync_CorrelationFound_SignsAndDeposits()
    {
        var rawCdc = """{"IdSystemA":"A1","MessageId":"m-a","Payload":"<xmlA/>"}""";
        _sentRepoMock.Setup(r => r.FindByIdSystemAAsync("A1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SpiSentMsg.Create("A1", "B1", CorrelationSource.Orchestrator));
        _cacheMock.Setup(c => c.GetAsync("request:B1", It.IsAny<CancellationToken>()))
            .ReturnsAsync("<xmlB/>");
        
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));

        _hsmMock.Setup(h => h.GetSigningCertificateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(cert);
        _signingMock.Setup(s => s.SignAsync(It.IsAny<XDocument>(), It.IsAny<X509Certificate2>()))
            .ReturnsAsync("<signed/>");

        await _sut.ExecuteAsync(rawCdc, CancellationToken.None);

        _cacheMock.Verify(c => c.SetAsync("B1", "<signed/>", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        _cacheMock.Verify(c => c.SignalResponseAsync("B1", "<signed/>", It.IsAny<CancellationToken>()), Times.Once);
        _publisherMock.Verify(p => p.PublishAsync("spi.systemb.responses", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
        _publisherMock.Verify(p => p.PublishAsync("spi.comparison.events", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_CorrelationNotFoundAfterRetries_ThrowsException()
    {
        var rawCdc = """{"IdSystemA":"A1","MessageId":"m-a","Payload":"<xmlA/>"}""";
        _sentRepoMock.Setup(r => r.FindByIdSystemAAsync("A1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiSentMsg?)null);

        var act = () => _sut.ExecuteAsync(rawCdc, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
