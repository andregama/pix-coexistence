using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Application.Parsing;
using ConvivenciaPix.V2.Application.Routing;
using ConvivenciaPix.V2.Application.UseCases.ProcessInboundMessage;
using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Domain.Messages;
using ConvivenciaPix.V2.Domain.Repositories;
using ConvivenciaPix.V2.Domain.Routing;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Text;
using Xunit;

namespace ConvivenciaPix.V2.Application.Tests.UseCases;

public sealed class ProcessInboundMessageUseCaseTests
{
    private static readonly byte[] Payload = "<xml/>"u8.ToArray();
    private static readonly TxKey Key = new(TxKeyKind.E2E, "E2E-1");

    private readonly Mock<ISpiMessageParser> _parser = new();
    private readonly Mock<IInboundRouter> _router = new();
    private readonly Mock<ISpiMessageSigner> _signer = new();
    private readonly Mock<IInboundMessageSplitter> _splitter = new();
    private readonly Mock<IInboundMessageRepository> _messages = new();
    private readonly Mock<IDeliveryNotifier> _notifier = new();
    private readonly Mock<IProxyMetrics> _metrics = new();
    private readonly InboundSigningOptions _signing = new();

    private SpiInboundMsg? _stored;
    private IReadOnlyList<InboundDelivery> _deliveries = [];
    private IReadOnlyList<TxBinding> _bindings = [];

    public ProcessInboundMessageUseCaseTests()
    {
        _parser.Setup(p => p.Parse(It.IsAny<ReadOnlyMemory<byte>>()))
            .Returns(ParsedSpiMessage.Single("pacs.008", "M-IN", ["M-ORIG"], [Key], "11122233344"));
        _router.Setup(r => r.RouteAsync(It.IsAny<ParsedSpiMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingDecision.To(SystemName.SystemB, RouteSources.DocumentRegistry) with
            {
                Bindings = [new TxBinding(Key, SystemName.SystemB)]
            });

        // "signed:<content>" makes it visible what was signed.
        _signer.Setup(s => s.Verify(It.IsAny<byte[]>())).Returns(true);
        _signer.Setup(s => s.Sign(It.IsAny<byte[]>()))
            .Returns((byte[] xml) => Encoding.UTF8.GetBytes("signed:" + Encoding.UTF8.GetString(xml)));
        _splitter.Setup(s => s.Extract(It.IsAny<byte[]>(), It.IsAny<IReadOnlyCollection<int>>()))
            .Returns((byte[] _, IReadOnlyCollection<int> indexes) => Encoding.UTF8.GetBytes("part[" + string.Join(",", indexes) + "]"));

        _messages.Setup(m => m.PersistAsync(
                It.IsAny<SpiInboundMsg>(), It.IsAny<IReadOnlyList<InboundDelivery>>(), It.IsAny<IReadOnlyList<TxBinding>>(),
                It.IsAny<AuditEvent?>(), It.IsAny<CancellationToken>()))
            .Callback((SpiInboundMsg message, IReadOnlyList<InboundDelivery> deliveries, IReadOnlyList<TxBinding> bindings,
                AuditEvent? _, CancellationToken _) => (_stored, _deliveries, _bindings) = (message, deliveries, bindings))
            .ReturnsAsync(true);
    }

    private ProcessInboundMessageUseCase UseCase() => new(
        _parser.Object, _router.Object, _signer.Object, _splitter.Object, _messages.Object, _notifier.Object,
        _metrics.Object, Options.Create(_signing), NullLogger<ProcessInboundMessageUseCase>.Instance);

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static ParsedSpiMessage Batch(int transactions) => new(
        "pacs.008", "M-BATCH", [],
        Enumerable.Range(0, transactions)
            .Select(i => new ParsedTransaction([new TxKey(TxKeyKind.E2E, "E" + i)], null, null, null)).ToList(),
        null);

    [Fact]
    public async Task KeepsWhatBacenSent_AndDeliversItUnderTheBanksSignature()
    {
        var decision = await UseCase().ExecuteAsync("RES-1", Payload);

        decision!.Targets.Should().Equal(SystemName.SystemB);
        _stored.Should().BeEquivalentTo(new
        {
            PiResourceId = "RES-1",
            MsgId = "M-IN",
            MsgType = "pacs.008",
            TxKey = "E2E-1",
            OriginalMsgId = "M-ORIG",
            RouteSource = RouteSources.DocumentRegistry,
            Targets = "SystemB"
        });
        _stored!.Payload.Should().BeSameAs(Payload, "the original is the evidence of what Bacen signed");
        _deliveries.Should().ContainSingle();
        _deliveries[0].System.Should().Be(SystemName.SystemB);
        Text(_deliveries[0].Payload).Should().Be("signed:<xml/>");
        _bindings.Should().Equal(new TxBinding(Key, SystemName.SystemB));
        _notifier.Verify(n => n.NotifyAsync(SystemName.SystemB, It.IsAny<CancellationToken>()), Times.Once);
        _notifier.Verify(n => n.NotifyAsync(SystemName.SystemA, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AlreadyStored_IsSkippedWithoutVerifyingRoutingOrSigningAgain()
    {
        _messages.Setup(m => m.ExistsAsync("RES-1", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var decision = await UseCase().ExecuteAsync("RES-1", Payload);

        decision.Should().BeNull();
        _router.Verify(r => r.RouteAsync(It.IsAny<ParsedSpiMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        _signer.Verify(s => s.Sign(It.IsAny<byte[]>()), Times.Never);
        _metrics.Verify(m => m.InboundDuplicate());
    }

    [Fact]
    public async Task Broadcast_SignsOnce_AndBothSystemsGetTheSameContent()
    {
        _router.Setup(r => r.RouteAsync(It.IsAny<ParsedSpiMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingDecision.ToBoth(RouteSources.Broadcast));

        await UseCase().ExecuteAsync("RES-1", Payload);

        _deliveries.Select(d => d.System).Should().Equal(SystemName.SystemA, SystemName.SystemB);
        _deliveries[0].Payload.Should().BeSameAs(_deliveries[1].Payload);
        _signer.Verify(s => s.Sign(It.IsAny<byte[]>()), Times.Once);
        _bindings.Should().BeEmpty();
        _notifier.Verify(n => n.NotifyAsync(It.IsAny<SystemName>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SplitMessage_EachSystemGetsOnlyItsTransactions_EachSignedSeparately()
    {
        _parser.Setup(p => p.Parse(It.IsAny<ReadOnlyMemory<byte>>())).Returns(Batch(3));
        _router.Setup(r => r.RouteAsync(It.IsAny<ParsedSpiMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoutingDecision([SystemName.SystemA, SystemName.SystemB], RouteSources.Split)
            {
                Parts = [new RoutingPart(SystemName.SystemA, [0, 2]), new RoutingPart(SystemName.SystemB, [1])]
            });

        await UseCase().ExecuteAsync("RES-1", Payload);

        _deliveries.Select(d => (d.System, Text(d.Payload))).Should().Equal(
            (SystemName.SystemA, "signed:part[0,2]"),
            (SystemName.SystemB, "signed:part[1]"));
        _stored!.Payload.Should().BeSameAs(Payload);
        _stored.RouteSource.Should().Be(RouteSources.Split);
    }

    [Fact]
    public async Task SplitMessage_SystemThatOwnsEveryTransaction_GetsTheWholeMessage()
    {
        // Transaction 1 is for both systems, transaction 0 only for A: A's part is the whole message.
        _parser.Setup(p => p.Parse(It.IsAny<ReadOnlyMemory<byte>>())).Returns(Batch(2));
        _router.Setup(r => r.RouteAsync(It.IsAny<ParsedSpiMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoutingDecision([SystemName.SystemA, SystemName.SystemB], RouteSources.Split)
            {
                Parts = [new RoutingPart(SystemName.SystemA, [0, 1]), new RoutingPart(SystemName.SystemB, [1])]
            });

        await UseCase().ExecuteAsync("RES-1", Payload);

        Text(_deliveries[0].Payload).Should().Be("signed:<xml/>");
        Text(_deliveries[1].Payload).Should().Be("signed:part[1]");
    }

    [Fact]
    public async Task BacenSignatureDoesNotVerify_MessageIsKeptButDeliveredToNobody()
    {
        _signer.Setup(s => s.Verify(Payload)).Returns(false);

        var decision = await UseCase().ExecuteAsync("RES-1", Payload);

        decision.Should().BeNull("the batch is still acknowledged: a bad message must not block the stream");
        _stored!.RouteSource.Should().Be(RouteSources.InvalidSignature);
        _stored.Targets.Should().BeEmpty();
        _stored.Payload.Should().BeSameAs(Payload);
        _deliveries.Should().BeEmpty();
        _bindings.Should().BeEmpty();
        _signer.Verify(s => s.Sign(It.IsAny<byte[]>()), Times.Never);
        _router.Verify(r => r.RouteAsync(It.IsAny<ParsedSpiMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        _metrics.Verify(m => m.InboundQuarantined("pacs.008", RouteSources.InvalidSignature));
        _notifier.Verify(n => n.NotifyAsync(It.IsAny<SystemName>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VerificationTurnedOff_DoesNotAskTheHsmToVerify()
    {
        _signing.VerifyBacenSignature = false;
        _signer.Setup(s => s.Verify(It.IsAny<byte[]>())).Returns(false);

        var decision = await UseCase().ExecuteAsync("RES-1", Payload);

        decision.Should().NotBeNull();
        _signer.Verify(s => s.Verify(It.IsAny<byte[]>()), Times.Never);
    }

    [Fact]
    public async Task ContentThatIsNotXml_IsQuarantined_BecauseItCannotBeSigned()
    {
        _parser.Setup(p => p.Parse(It.IsAny<ReadOnlyMemory<byte>>())).Returns(ParsedSpiMessage.Unparseable);

        var decision = await UseCase().ExecuteAsync("RES-1", Payload);

        decision.Should().BeNull();
        _stored!.RouteSource.Should().Be(RouteSources.Unreadable);
        _deliveries.Should().BeEmpty();
        _signer.Verify(s => s.Verify(It.IsAny<byte[]>()), Times.Never);
    }

    [Fact]
    public async Task HsmUnavailable_Throws_SoTheBatchIsNotAcknowledged()
    {
        _signer.Setup(s => s.Sign(It.IsAny<byte[]>())).Throws(new InvalidOperationException("hsm down"));

        var act = () => UseCase().ExecuteAsync("RES-1", Payload);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _messages.Verify(m => m.PersistAsync(
            It.IsAny<SpiInboundMsg>(), It.IsAny<IReadOnlyList<InboundDelivery>>(), It.IsAny<IReadOnlyList<TxBinding>>(),
            It.IsAny<AuditEvent?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StorageFails_Throws_SoTheBatchIsNotAcknowledged()
    {
        _messages.Setup(m => m.PersistAsync(
                It.IsAny<SpiInboundMsg>(), It.IsAny<IReadOnlyList<InboundDelivery>>(), It.IsAny<IReadOnlyList<TxBinding>>(),
                It.IsAny<AuditEvent?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database down"));

        var act = () => UseCase().ExecuteAsync("RES-1", Payload);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task NotificationFails_MessageIsStillStored()
    {
        _notifier.Setup(n => n.NotifyAsync(It.IsAny<SystemName>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));

        var decision = await UseCase().ExecuteAsync("RES-1", Payload);

        decision.Should().NotBeNull();
    }
}
