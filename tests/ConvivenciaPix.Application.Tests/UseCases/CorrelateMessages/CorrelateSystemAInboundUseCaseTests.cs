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

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();

    // Builds the inbound classification bundle; defaults to allowing only pacs.002 with no special
    // routing, so each test overrides just the sets it exercises.
    private static InboundCorrelationTypes Types(
        IReadOnlySet<string>? allowed = null,
        IReadOnlySet<string>? primary = null,
        IReadOnlySet<string>? byMsgId = null,
        IReadOnlySet<string>? byEndToEnd = null) =>
        new(allowed ?? AllowedTypes, primary ?? Empty, byMsgId ?? Empty, byEndToEnd ?? Empty);

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

        await _sut.ExecuteAsync(CdcJson, Types(), CancellationToken.None);

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
    public async Task ExecuteAsync_Camt025_CorrelatesToTrck002Pair_AndPublishes()
    {
        // The inbound response (camt.025) and the stored request pair (trck.002) have different
        // MsgTypes; correlation is purely by the shared key, so the use case must handle the mismatch.
        var allowed = new HashSet<string> { "camt.025" };
        var trckPair = SpiSentMsg.Create("E2E-INTERNAL", "trck.002");
        trckPair.UpdateFromSystemA("TRCK-A", "<trck-a/>", null);
        trckPair.UpdateFromSystemB("TRCK-B", "<trck-b/>", null);

        _xmlParserMock.Setup(p => p.ExtractMessageType(It.IsAny<string>())).Returns("camt.025");
        _xmlParserMock.Setup(p => p.ExtractCorrelationKey(It.IsAny<string>(), "camt.025")).Returns("E2E-INTERNAL");
        _xmlParserMock.Setup(p => p.GetCorrelationSource("camt.025")).Returns("MessageKey");
        _xmlParserMock.Setup(p => p.ExtractOriginalIdempotentId(It.IsAny<string>(), "camt.025")).Returns("TRCK-A");
        _receivedRepoMock.Setup(r => r.FindByIdempotentIdAsync("E2E-INTERNAL", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiReceivedMsg?)null);
        _sentRepoMock.Setup(r => r.FindByIdempotentIdAsync("E2E-INTERNAL", It.IsAny<CancellationToken>()))
            .ReturnsAsync(trckPair);
        _transformerMock.Setup(t => t.Transform("<pacs002/>", "<trck-a/>", "<trck-b/>"))
            .Returns("<camt025-for-b/>");

        KafkaEnvelope? published = null;
        _publisherMock
            .Setup(p => p.PublishAsync("spi.systemb.responses", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<string, KafkaEnvelope, CancellationToken>((_, e, _) => published = e)
            .Returns(Task.CompletedTask);

        await _sut.ExecuteAsync(CdcJson, Types(allowed: allowed), CancellationToken.None);

        _transformerMock.Verify(t => t.Transform("<pacs002/>", "<trck-a/>", "<trck-b/>"), Times.Once);
        published.Should().NotBeNull();
        var ready = JsonSerializer.Deserialize<SystemBInboundReadyDto>(
            Encoding.UTF8.GetString(Convert.FromBase64String(published!.PayloadBase64)))!;
        ready.IdempotentId.Should().Be("E2E-INTERNAL");
        ready.MsgType.Should().Be("camt.025");
        ready.TransformedXml.Should().Be("<camt025-for-b/>");
    }

    [Fact]
    public async Task ExecuteAsync_PrimaryInbound_PassesThroughToSystemB_WithoutCorrelation()
    {
        // A received pacs.008 (incoming Pix) is a primary message: no SpiSentMsg exists. It must be
        // stored and passed through to System B unchanged — never looked up in SpiSentMsg or transformed.
        var allowed = new HashSet<string> { "pacs.008" };
        var primary = new HashSet<string> { "pacs.008" };
        var cdc = JsonSerializer.Serialize(new { after = new { XmlMsg = "<pacs008-incoming/>", Problem = (string?)null } });

        _xmlParserMock.Setup(p => p.ExtractMessageType(It.IsAny<string>())).Returns("pacs.008");
        _xmlParserMock.Setup(p => p.ExtractCorrelationKey(It.IsAny<string>(), "pacs.008")).Returns("E2E-INCOMING");
        _xmlParserMock.Setup(p => p.GetCorrelationSource("pacs.008")).Returns("MessageKey");
        _xmlParserMock.Setup(p => p.ExtractOriginalIdempotentId(It.IsAny<string>(), "pacs.008")).Returns((string?)null);
        _receivedRepoMock.Setup(r => r.FindByIdempotentIdAsync("E2E-INCOMING", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiReceivedMsg?)null);

        KafkaEnvelope? published = null;
        _publisherMock
            .Setup(p => p.PublishAsync("spi.systemb.responses", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<string, KafkaEnvelope, CancellationToken>((_, e, _) => published = e)
            .Returns(Task.CompletedTask);

        await _sut.ExecuteAsync(cdc, Types(allowed: allowed, primary: primary), CancellationToken.None);

        _receivedRepoMock.Verify(r => r.AddAsync(It.IsAny<SpiReceivedMsg>(), It.IsAny<CancellationToken>()), Times.Once);
        // No SpiSentMsg correlation and no transform for a primary message.
        _sentRepoMock.Verify(r => r.FindByIdempotentIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _transformerMock.Verify(t => t.Transform(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        published.Should().NotBeNull();
        var ready = JsonSerializer.Deserialize<SystemBInboundReadyDto>(
            Encoding.UTF8.GetString(Convert.FromBase64String(published!.PayloadBase64)))!;
        ready.IdempotentId.Should().Be("E2E-INCOMING");
        ready.MsgType.Should().Be("pacs.008");
        ready.TransformedXml.Should().Be("<pacs008-incoming/>"); // unchanged pass-through
    }

    [Fact]
    public async Task ExecuteAsync_Admi002_CorrelatesByOriginalMsgId_AndTransforms()
    {
        // admi.002 references the rejected message by its MsgId (RltdRef/Ref); correlation is a
        // SpiSentMsg lookup by MsgIdSystemA, not by IdempotentId.
        var allowed = new HashSet<string> { "admi.002" };
        var byMsgId = new HashSet<string> { "admi.002" };
        var original = SpiSentMsg.Create("E2E-ORIG", "pacs.008");
        original.UpdateFromSystemA("MSG-A", "<orig-a/>", null);
        original.UpdateFromSystemB("MSG-B", "<orig-b/>", null);

        _xmlParserMock.Setup(p => p.ExtractMessageType(It.IsAny<string>())).Returns("admi.002");
        _xmlParserMock.Setup(p => p.ExtractCorrelationKey(It.IsAny<string>(), "admi.002")).Returns("ADMI-OWN");
        _xmlParserMock.Setup(p => p.GetCorrelationSource("admi.002")).Returns("MessageKey");
        _xmlParserMock.Setup(p => p.ExtractOriginalIdempotentId(It.IsAny<string>(), "admi.002")).Returns("MSG-A");
        _receivedRepoMock.Setup(r => r.FindByIdempotentIdAsync("ADMI-OWN", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiReceivedMsg?)null);
        _sentRepoMock.Setup(r => r.FindByMsgIdSystemAAsync("MSG-A", It.IsAny<CancellationToken>()))
            .ReturnsAsync(original);
        _transformerMock.Setup(t => t.Transform("<pacs002/>", "<orig-a/>", "<orig-b/>"))
            .Returns("<admi002-for-b/>");

        KafkaEnvelope? published = null;
        _publisherMock
            .Setup(p => p.PublishAsync("spi.systemb.responses", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<string, KafkaEnvelope, CancellationToken>((_, e, _) => published = e)
            .Returns(Task.CompletedTask);

        await _sut.ExecuteAsync(CdcJson, Types(allowed: allowed, byMsgId: byMsgId), CancellationToken.None);

        _sentRepoMock.Verify(r => r.FindByMsgIdSystemAAsync("MSG-A", It.IsAny<CancellationToken>()), Times.Once);
        // Never falls through to the by-IdempotentId correlation path.
        _sentRepoMock.Verify(r => r.FindByIdempotentIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        published.Should().NotBeNull();
        JsonSerializer.Deserialize<SystemBInboundReadyDto>(
            Encoding.UTF8.GetString(Convert.FromBase64String(published!.PayloadBase64)))!
            .TransformedXml.Should().Be("<admi002-for-b/>");
    }

    [Fact]
    public async Task ExecuteAsync_Admi002_OriginalMsgIdNotFound_ReplicatesUnchanged_WithoutThrowing()
    {
        // Even when the referenced MsgId is not in SpiSentMsg, the message must still be replicated to
        // System B (unchanged) rather than dead-lettered.
        var allowed = new HashSet<string> { "admi.002" };
        var byMsgId = new HashSet<string> { "admi.002" };
        var cdc = JsonSerializer.Serialize(new { after = new { XmlMsg = "<admi002-raw/>", Problem = (string?)null } });

        _xmlParserMock.Setup(p => p.ExtractMessageType(It.IsAny<string>())).Returns("admi.002");
        _xmlParserMock.Setup(p => p.ExtractCorrelationKey(It.IsAny<string>(), "admi.002")).Returns("ADMI-OWN");
        _xmlParserMock.Setup(p => p.GetCorrelationSource("admi.002")).Returns("MessageKey");
        _xmlParserMock.Setup(p => p.ExtractOriginalIdempotentId(It.IsAny<string>(), "admi.002")).Returns("MSG-UNKNOWN");
        _receivedRepoMock.Setup(r => r.FindByIdempotentIdAsync("ADMI-OWN", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiReceivedMsg?)null);
        _sentRepoMock.Setup(r => r.FindByMsgIdSystemAAsync("MSG-UNKNOWN", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiSentMsg?)null);

        KafkaEnvelope? published = null;
        _publisherMock
            .Setup(p => p.PublishAsync("spi.systemb.responses", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<string, KafkaEnvelope, CancellationToken>((_, e, _) => published = e)
            .Returns(Task.CompletedTask);

        await _sut.ExecuteAsync(cdc, Types(allowed: allowed, byMsgId: byMsgId), CancellationToken.None);

        _transformerMock.Verify(t => t.Transform(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        published.Should().NotBeNull();
        JsonSerializer.Deserialize<SystemBInboundReadyDto>(
            Encoding.UTF8.GetString(Convert.FromBase64String(published!.PayloadBase64)))!
            .TransformedXml.Should().Be("<admi002-raw/>"); // unchanged pass-through
    }

    [Fact]
    public async Task ExecuteAsync_Pacs004_CorrelatesToOriginalTransfer_ByOrgnlEndToEndId_AndTransforms()
    {
        // A received pacs.004 return references the original transfer via OrgnlEndToEndId; correlation
        // is a SpiSentMsg.IdempotentId lookup on that value. Its own id (RtrId) stays the received-msg key.
        var allowed = new HashSet<string> { "pacs.004" };
        var byE2E = new HashSet<string> { "pacs.004" };
        var original = SpiSentMsg.Create("E2E-ORIGINAL", "pacs.008");
        original.UpdateFromSystemA("MSG-A", "<pacs008-a/>", null);
        original.UpdateFromSystemB("MSG-B", "<pacs008-b/>", null);

        _xmlParserMock.Setup(p => p.ExtractMessageType(It.IsAny<string>())).Returns("pacs.004");
        _xmlParserMock.Setup(p => p.ExtractCorrelationKey(It.IsAny<string>(), "pacs.004")).Returns("RTR-1"); // return's own id
        _xmlParserMock.Setup(p => p.GetCorrelationSource("pacs.004")).Returns("MessageKey");
        _xmlParserMock.Setup(p => p.ExtractOriginalIdempotentId(It.IsAny<string>(), "pacs.004")).Returns("E2E-ORIGINAL");
        _receivedRepoMock.Setup(r => r.FindByIdempotentIdAsync("RTR-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiReceivedMsg?)null);
        _sentRepoMock.Setup(r => r.FindByIdempotentIdAsync("E2E-ORIGINAL", It.IsAny<CancellationToken>()))
            .ReturnsAsync(original);
        _transformerMock.Setup(t => t.Transform("<pacs002/>", "<pacs008-a/>", "<pacs008-b/>"))
            .Returns("<pacs004-for-b/>");

        KafkaEnvelope? published = null;
        _publisherMock
            .Setup(p => p.PublishAsync("spi.systemb.responses", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<string, KafkaEnvelope, CancellationToken>((_, e, _) => published = e)
            .Returns(Task.CompletedTask);

        await _sut.ExecuteAsync(CdcJson, Types(allowed: allowed, byEndToEnd: byE2E), CancellationToken.None);

        _sentRepoMock.Verify(r => r.FindByIdempotentIdAsync("E2E-ORIGINAL", It.IsAny<CancellationToken>()), Times.Once);
        published.Should().NotBeNull();
        var ready = JsonSerializer.Deserialize<SystemBInboundReadyDto>(
            Encoding.UTF8.GetString(Convert.FromBase64String(published!.PayloadBase64)))!;
        ready.IdempotentId.Should().Be("RTR-1"); // keyed by the return id, not the original
        ready.TransformedXml.Should().Be("<pacs004-for-b/>");
    }

    [Fact]
    public async Task ExecuteAsync_Pacs004_OriginalTransferNotFound_ReplicatesUnchanged_WithoutThrowing()
    {
        // Returns can arrive after the 30-day SpiSentMsg TTL; the original is gone. The pacs.004 must
        // still be replicated to System B unchanged, not dead-lettered.
        var allowed = new HashSet<string> { "pacs.004" };
        var byE2E = new HashSet<string> { "pacs.004" };
        var cdc = JsonSerializer.Serialize(new { after = new { XmlMsg = "<pacs004-raw/>", Problem = (string?)null } });

        _xmlParserMock.Setup(p => p.ExtractMessageType(It.IsAny<string>())).Returns("pacs.004");
        _xmlParserMock.Setup(p => p.ExtractCorrelationKey(It.IsAny<string>(), "pacs.004")).Returns("RTR-2");
        _xmlParserMock.Setup(p => p.GetCorrelationSource("pacs.004")).Returns("MessageKey");
        _xmlParserMock.Setup(p => p.ExtractOriginalIdempotentId(It.IsAny<string>(), "pacs.004")).Returns("E2E-EXPIRED");
        _receivedRepoMock.Setup(r => r.FindByIdempotentIdAsync("RTR-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiReceivedMsg?)null);
        _sentRepoMock.Setup(r => r.FindByIdempotentIdAsync("E2E-EXPIRED", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiSentMsg?)null);

        KafkaEnvelope? published = null;
        _publisherMock
            .Setup(p => p.PublishAsync("spi.systemb.responses", It.IsAny<KafkaEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<string, KafkaEnvelope, CancellationToken>((_, e, _) => published = e)
            .Returns(Task.CompletedTask);

        await _sut.ExecuteAsync(cdc, Types(allowed: allowed, byEndToEnd: byE2E), CancellationToken.None);

        _transformerMock.Verify(t => t.Transform(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        published.Should().NotBeNull();
        JsonSerializer.Deserialize<SystemBInboundReadyDto>(
            Encoding.UTF8.GetString(Convert.FromBase64String(published!.PayloadBase64)))!
            .TransformedXml.Should().Be("<pacs004-raw/>"); // unchanged pass-through
    }

    [Fact]
    public async Task ExecuteAsync_Throws_WhenSentMsgMissing()
    {
        _receivedRepoMock.Setup(r => r.FindByIdempotentIdAsync("E2E-A", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiReceivedMsg?)null);
        _sentRepoMock.Setup(r => r.FindByIdempotentIdAsync("E2E-A", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpiSentMsg?)null);

        await FluentActions.Invoking(() => _sut.ExecuteAsync(CdcJson, Types(), CancellationToken.None))
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

        await _sut.ExecuteAsync(CdcJson, Types(), CancellationToken.None);

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

        await FluentActions.Invoking(() => _sut.ExecuteAsync(CdcJson, Types(), CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExecuteAsync_Skips_WhenMsgTypeNotAllowed()
    {
        _xmlParserMock.Setup(p => p.ExtractMessageType(It.IsAny<string>())).Returns("pacs.999");

        await _sut.ExecuteAsync(CdcJson, Types(), CancellationToken.None);

        _receivedRepoMock.Verify(r => r.AddAsync(It.IsAny<SpiReceivedMsg>(), It.IsAny<CancellationToken>()), Times.Never);
        _publisherMock.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<KafkaEnvelope>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
