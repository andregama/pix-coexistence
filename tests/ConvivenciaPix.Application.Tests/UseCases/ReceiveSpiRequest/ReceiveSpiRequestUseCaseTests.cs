using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.ReceiveSpiRequest;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ConvivenciaPix.Application.Tests.UseCases.ReceiveSpiRequest;

public sealed class ReceiveSpiRequestUseCaseTests
{
    private readonly Mock<IResponseCache> _cacheMock = new();
    private readonly Mock<IKafkaPublisher> _publisherMock = new();
    private readonly Mock<ISpiXmlParser> _xmlParserMock = new();
    private readonly Mock<IValidator<string>> _validatorMock = new();
    private readonly Mock<ILogger<ReceiveSpiRequestUseCase>> _loggerMock = new();
    private readonly IOptions<SpiProxyOptions> _options = Options.Create(new SpiProxyOptions { TimeoutSeconds = 1 });

    private readonly ReceiveSpiRequestUseCase _sut;

    public ReceiveSpiRequestUseCaseTests()
    {
        _sut = new ReceiveSpiRequestUseCase(
            _cacheMock.Object,
            _publisherMock.Object,
            _xmlParserMock.Object,
            _validatorMock.Object,
            _options,
            _loggerMock.Object);
    }

    [Fact]
    public async Task ExecuteAsync_ValidationFails_ReturnsError()
    {
        _validatorMock.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(new[] { new ValidationFailure("xml", "Invalid XML") }));

        var result = await _sut.ExecuteAsync("<invalid/>", CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("SPI2023");
        result.ErrorReason.Should().Be("Invalid XML");
    }

    [Fact]
    public async Task ExecuteAsync_IdempotencyHit_ReturnsCachedResponse()
    {
        _validatorMock.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        _xmlParserMock.Setup(p => p.ExtractMessageId(It.IsAny<string>())).Returns("msg-123");
        _xmlParserMock.Setup(p => p.ExtractEndToEndId(It.IsAny<string>())).Returns("sysb-123");
        _cacheMock.Setup(c => c.GetIdempotencyKeyAsync("msg-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync("<cached-response/>");

        var result = await _sut.ExecuteAsync("<request/>", CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.ResponseXml.Should().Be("<cached-response/>");
        _publisherMock.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_NormalFlow_PublishesAndWaits()
    {
        _validatorMock.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        _xmlParserMock.Setup(p => p.ExtractMessageId(It.IsAny<string>())).Returns("msg-123");
        _xmlParserMock.Setup(p => p.ExtractEndToEndId(It.IsAny<string>())).Returns("sysb-123");
        _cacheMock.Setup(c => c.GetIdempotencyKeyAsync("msg-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _cacheMock.Setup(c => c.WaitForResponseAsync("sysb-123", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<response/>");

        var result = await _sut.ExecuteAsync("<request/>", CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.ResponseXml.Should().Be("<response/>");
        _publisherMock.Verify(p => p.PublishAsync("spi.systemb.requests", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
        _cacheMock.Verify(c => c.SetIdempotencyKeyAsync("msg-123", "<response/>", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Timeout_Returns504Error()
    {
        _validatorMock.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        _xmlParserMock.Setup(p => p.ExtractMessageId(It.IsAny<string>())).Returns("msg-123");
        _xmlParserMock.Setup(p => p.ExtractEndToEndId(It.IsAny<string>())).Returns("sysb-123");
        _cacheMock.Setup(c => c.WaitForResponseAsync("sysb-123", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var result = await _sut.ExecuteAsync("<request/>", CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("SPI9999");
        result.ErrorReason.Should().Be("Processing timeout");
    }
}
