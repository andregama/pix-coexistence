using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.PropagateResponse;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace ConvivenciaPix.Application.Tests.UseCases.PropagateResponse;

public sealed class PropagateResponseUseCaseTests
{
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock = new();
    private readonly Mock<IServiceScope> _scopeMock = new();
    private readonly Mock<IServiceProvider> _serviceProviderMock = new();
    private readonly Mock<IOutboundStream> _outboundMock = new();
    private readonly Mock<IHsmService> _hsmMock = new();
    private readonly Mock<IXmlSigningService> _signingMock = new();
    private readonly Mock<IKafkaPublisher> _publisherMock = new();
    private readonly Mock<ISpiMetrics> _metricsMock = new();
    private readonly Mock<ISpiXmlParser> _xmlParserMock = new();
    private readonly Mock<ISpiReceivedMsgRepository> _receivedRepoMock = new();

    private readonly PropagateResponseUseCase _sut;

    private static readonly string ValidCdcJson = JsonSerializer.Serialize(new
    {
        after = new { XmlMsg = "<pacs/>", Problem = (string?)null }
    });

    public PropagateResponseUseCaseTests()
    {
        // CreateAsyncScope() is an extension method that calls CreateScope() internally
        _scopeFactoryMock.Setup(f => f.CreateScope()).Returns(_scopeMock.Object);
        _scopeMock.Setup(s => s.ServiceProvider).Returns(_serviceProviderMock.Object);
        _serviceProviderMock
            .Setup(s => s.GetService(typeof(ISpiReceivedMsgRepository)))
            .Returns(_receivedRepoMock.Object);

        _xmlParserMock.Setup(p => p.ExtractMessageType(It.IsAny<string>())).Returns("pacs.002");
        _xmlParserMock.Setup(p => p.ExtractIdempotentId(It.IsAny<string>(), "pacs.002"))
            .Returns("E123456789");
        _xmlParserMock.Setup(p => p.ExtractOriginalIdempotentId(It.IsAny<string>(), "pacs.002"))
            .Returns((string?)null);

        _sut = new PropagateResponseUseCase(
            _scopeFactoryMock.Object,
            _outboundMock.Object,
            _hsmMock.Object,
            _signingMock.Object,
            _publisherMock.Object,
            _xmlParserMock.Object,
            _metricsMock.Object,
            NullLogger<PropagateResponseUseCase>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_SignsAndEnqueuesXml()
    {
        SetupCert("<signed/>");
        _receivedRepoMock.Setup(r => r.FindByIdempotentIdAsync("E123456789", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiReceivedMsg?)null);

        await _sut.ExecuteAsync(ValidCdcJson, CancellationToken.None);

        _outboundMock.Verify(
            o => o.EnqueueAsync(It.IsAny<string>(), "<signed/>", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenComplete_PublishesComparisonEvent()
    {
        SetupCert("<signed/>");

        var existing = SpiReceivedMsg.CreateFromSystemA("E123456789", "pacs.002", null, "<xmlA/>", null);
        _receivedRepoMock.Setup(r => r.FindByIdempotentIdAsync("E123456789", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        await _sut.ExecuteAsync(ValidCdcJson, CancellationToken.None);

        _publisherMock.Verify(
            p => p.PublishAsync("spi.comparison.events", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNotComplete_DoesNotPublishComparisonEvent()
    {
        SetupCert("<signed/>");
        _receivedRepoMock.Setup(r => r.FindByIdempotentIdAsync("E123456789", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiReceivedMsg?)null);

        await _sut.ExecuteAsync(ValidCdcJson, CancellationToken.None);

        _publisherMock.Verify(
            p => p.PublishAsync("spi.comparison.events", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void SetupCert(string signedXml)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
        _hsmMock.Setup(h => h.GetSigningCertificateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(cert);
        _signingMock.Setup(s => s.SignAsync(It.IsAny<XDocument>(), It.IsAny<X509Certificate2>()))
            .ReturnsAsync(signedXml);
    }
}
