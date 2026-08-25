using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.CorrelateMessages;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ConvivenciaPix.Application.Tests.UseCases.CorrelateMessages;

public sealed class CorrelateSystemAInboundUseCaseTests
{
    private readonly Mock<ISpiReceivedMsgRepository> _receivedRepoMock = new();
    private readonly Mock<ISpiSentMsgRepository> _sentRepoMock = new();
    private readonly Mock<ISpiXmlParser> _xmlParserMock = new();
    private readonly Mock<IInboundResponseTransformer> _transformerMock = new();
    private readonly Mock<IKafkaPublisher> _publisherMock = new();
    private readonly Mock<ISpiMetrics> _metricsMock = new();

    private readonly CorrelateSystemAInboundUseCase _sut;

    private static readonly IReadOnlySet<string> AllowedTypes =
        new HashSet<string> { "pacs.002" };

    private static readonly string CdcJson = JsonSerializer.Serialize(new
    {
        after = new { XmlMsg = "<pacs002/>", Problem = (string?)null }
    });

    public CorrelateSystemAInboundUseCaseTests()
    {
        _xmlParserMock.Setup(p => p.ExtractMessageType(It.IsAny<string>())).Returns("pacs.002");
        _xmlParserMock.Setup(p => p.ExtractCorrelationKey(It.IsAny<string>(), "pacs.002")).Returns("E2E-A");
        _xmlParserMock.Setup(p => p.GetCorrelationSource("pacs.002")).Returns("MessageKey");
        _xmlParserMock.Setup(p => p.ExtractOriginalIdempotentId(It.IsAny<string>(), "pacs.002"))
            .Returns((string?)null);
        _xmlParserMock.Setup(p => p.ExtractMessageId(It.IsAny<string>())).Returns("MSG-1");

        // Small attempt budget with zero delay keeps the retry-path tests instant.
        var retryOptions = Options.Create(new CorrelateInboundRetryOptions
        {
            MaxAttempts = 3,
            InitialDelayMs = 0,
            MaxDelayMs = 0,
        });

        _sut = new CorrelateSystemAInboundUseCase(
            _receivedRepoMock.Object,
            _sentRepoMock.Object,
            _xmlParserMock.Object,
            _transformerMock.Object,
            _publisherMock.Object,
            _metricsMock.Object,
            retryOptions,
            TimeProvider.System,
            NullLogger<CorrelateSystemAInboundUseCase>.Instance);
    }

    private static SpiSentMsg CompleteSentMsg()
    {
        var sent = SpiSentMsg.Create("E2E-A", "pacs.008");
        sent.UpdateFromSystemA("MSG-A", "<pacs008-a/>", null);
        sent.UpdateFromSystemB("MSG-B", "<pacs008-b/>", null);
        return sent;
    }

    [Fact]
    public async Task ExecuteAsync_StoresReceivedMsg_Transforms_AndPublishesReadyEvent()
    {
        _receivedRepoMock.Setup(r => r.FindByIdempotentIdAsync("E2E-A", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiReceivedMsg?)null);
        _sentRepoMock.Setup(r => r.FindByIdempotentIdAsync("E2E-A", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CompleteSentMsg());
        _transformerMock
            .Setup(t => t.Transform("<pacs002/>", "<pacs008-a/>", "<pacs008-b/>"))
            .Returns("<pacs002-transformed/>");

        KafkaEnvelope? published = null;
        _publisherMock
            .Setup(p => p.PublishAsync("spi.systemb.responses", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<string, KafkaEnvelope, CancellationToken>((_, e, _) => published = e)
            .Returns(Task.CompletedTask);

        await _sut.ExecuteAsync(CdcJson, AllowedTypes, CancellationToken.None);

        _receivedRepoMock.Verify(r => r.AddAsync(It.IsAny<SpiReceivedMsg>(), It.IsAny<CancellationToken>()), Times.Once);
        _transformerMock.Verify(t => t.Transform("<pacs002/>", "<pacs008-a/>", "<pacs008-b/>"), Times.Once);
        published.Should().NotBeNull();

        var readyJson = Encoding.UTF8.GetString(Convert.FromBase64String(published!.PayloadBase64));
        var ready = JsonSerializer.Deserialize<SystemBInboundReadyDto>(readyJson)!;
        ready.IdempotentId.Should().Be("E2E-A");
        ready.MsgType.Should().Be("pacs.002");
        ready.TransformedXml.Should().Be("<pacs002-transformed/>");
    }



    [Fact]
    public async Task ExecuteAsync_Throws_WhenSentMsgMissing()
    {
        _receivedRepoMock.Setup(r => r.FindByIdempotentIdAsync("E2E-A", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiReceivedMsg?)null);
        _sentRepoMock.Setup(r => r.FindByIdempotentIdAsync("E2E-A", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiSentMsg?)null);

        await FluentActions.Invoking(() => _sut.ExecuteAsync(CdcJson, AllowedTypes, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();

        // The lookup is retried up to MaxAttempts (3) before giving up to the DLQ.
        _sentRepoMock.Verify(r => r.FindByIdempotentIdAsync("E2E-A", It.IsAny<CancellationToken>()),
            Times.Exactly(3));
        _publisherMock.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<KafkaEnvelope>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_RetriesThenSucceeds_WhenSentRowCompletesLate()
    {
        var incomplete = SpiSentMsg.Create("E2E-A", "pacs.008");
        incomplete.UpdateFromSystemA("MSG-A", "<pacs008-a/>", null); // B side not persisted yet

        _receivedRepoMock.Setup(r => r.FindByIdempotentIdAsync("E2E-A", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiReceivedMsg?)null);

        // Simulates the race: row absent, then present-but-incomplete, then complete on the 3rd read.
        _sentRepoMock.SetupSequence(r => r.FindByIdempotentIdAsync("E2E-A", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiSentMsg?)null)
            .ReturnsAsync(incomplete)
            .ReturnsAsync(CompleteSentMsg());

        _transformerMock
            .Setup(t => t.Transform("<pacs002/>", "<pacs008-a/>", "<pacs008-b/>"))
            .Returns("<pacs002-transformed/>");

        KafkaEnvelope? published = null;
        _publisherMock
            .Setup(p => p.PublishAsync("spi.systemb.responses", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<string, KafkaEnvelope, CancellationToken>((_, e, _) => published = e)
            .Returns(Task.CompletedTask);

        await _sut.ExecuteAsync(CdcJson, AllowedTypes, CancellationToken.None);

        _sentRepoMock.Verify(r => r.FindByIdempotentIdAsync("E2E-A", It.IsAny<CancellationToken>()),
            Times.Exactly(3));
        _transformerMock.Verify(t => t.Transform("<pacs002/>", "<pacs008-a/>", "<pacs008-b/>"), Times.Once);
        published.Should().NotBeNull("the response must be delivered once the sent row completes");
    }

    [Fact]
    public async Task ExecuteAsync_Throws_WhenSentMsgIncomplete()
    {
        var incomplete = SpiSentMsg.Create("E2E-A", "pacs.008");
        incomplete.UpdateFromSystemA("MSG-A", "<pacs008-a/>", null); // B side missing

        _receivedRepoMock.Setup(r => r.FindByIdempotentIdAsync("E2E-A", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiReceivedMsg?)null);
        _sentRepoMock.Setup(r => r.FindByIdempotentIdAsync("E2E-A", It.IsAny<CancellationToken>()))
            .ReturnsAsync(incomplete);

        await FluentActions.Invoking(() => _sut.ExecuteAsync(CdcJson, AllowedTypes, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExecuteAsync_Skips_WhenMsgTypeNotAllowed()
    {
        _xmlParserMock.Setup(p => p.ExtractMessageType(It.IsAny<string>())).Returns("pacs.999");

        await _sut.ExecuteAsync(CdcJson, AllowedTypes, CancellationToken.None);

        _receivedRepoMock.Verify(r => r.AddAsync(It.IsAny<SpiReceivedMsg>(), It.IsAny<CancellationToken>()), Times.Never);
        _publisherMock.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<KafkaEnvelope>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
