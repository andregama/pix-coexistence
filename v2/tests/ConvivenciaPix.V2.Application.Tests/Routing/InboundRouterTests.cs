using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Application.Parsing;
using ConvivenciaPix.V2.Application.Routing;
using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Domain.Repositories;
using ConvivenciaPix.V2.Domain.Routing;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ConvivenciaPix.V2.Application.Tests.Routing;

public sealed class InboundRouterTests
{
    private const string EndToEndId = "E1234567820260101120000000000001";

    private readonly Mock<ISentRouteRepository> _sentRoutes = new();
    private readonly Mock<ITxAffinityRepository> _affinities = new();
    private readonly Mock<ISystemBDocumentRegistry> _registry = new();
    private readonly RoutingOptions _options = new();

    private InboundRouter Router()
    {
        var options = Options.Create(_options);
        IRouteResolver[] resolvers =
        [
            new SentByOriginalMsgIdResolver(_sentRoutes.Object),
            new SentByPiResourceIdResolver(_sentRoutes.Object),
            new TxAffinityResolver(_affinities.Object),
            new DocumentRegistryResolver(_registry.Object),
            new EchoDataResolver(_sentRoutes.Object),
            new BroadcastResolver(),
            new DefaultResolver(options)
        ];
        return new InboundRouter(new RoutingTable(options), resolvers, options);
    }

    private static ParsedSpiMessage Message(
        string msgType,
        string[]? originalRefs = null,
        TxKey[]? txKeys = null,
        string? creditorDocument = null,
        string? debtorDocument = null,
        string? echoData = null,
        string? ownSideDocument = null) =>
        ParsedSpiMessage.Single(
            msgType, "M-INBOUND", originalRefs ?? [], txKeys ?? [], creditorDocument, debtorDocument, echoData, ownSideDocument);

    [Fact]
    public async Task Response_GoesToTheSystemThatSentTheOriginalMessage()
    {
        _sentRoutes.Setup(r => r.FindSystemByMsgIdAsync("M-ORIGINAL", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SystemName.SystemB);

        var decision = await Router().RouteAsync(Message("pacs.002", originalRefs: ["M-ORIGINAL"]));

        decision.Targets.Should().Equal(SystemName.SystemB);
        decision.Source.Should().Be(RouteSources.SentByOriginalMsgId);
    }

    [Fact]
    public async Task Response_WithUnknownOriginal_FollowsTheTransaction()
    {
        _affinities.Setup(r => r.FindAsync(new TxKey(TxKeyKind.E2E, EndToEndId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SystemName.SystemB);

        var decision = await Router().RouteAsync(
            Message("pacs.002", originalRefs: ["M-UNKNOWN"], txKeys: [new TxKey(TxKeyKind.E2E, EndToEndId)]));

        decision.Targets.Should().Equal(SystemName.SystemB);
        decision.Source.Should().Be(RouteSources.TxAffinity);
    }

    [Fact]
    public async Task Response_NothingMatches_GoesToTheDefaultSystem()
    {
        var decision = await Router().RouteAsync(Message("pacs.002", originalRefs: ["M-UNKNOWN"]));

        decision.Targets.Should().Equal(SystemName.SystemA);
        decision.Source.Should().Be(RouteSources.Default);
    }

    [Fact]
    public async Task ReceivedTransfer_CreditorRegistered_GoesToSystemB()
    {
        _registry.Setup(r => r.Contains("11122233344")).Returns(true);

        var decision = await Router().RouteAsync(Message("pacs.008", creditorDocument: "11122233344"));

        decision.Targets.Should().Equal(SystemName.SystemB);
        decision.Source.Should().Be(RouteSources.DocumentRegistry);
    }

    [Fact]
    public async Task ReceivedTransfer_CreditorNotRegistered_GoesToSystemA()
    {
        var decision = await Router().RouteAsync(Message("pacs.008", creditorDocument: "11122233344"));

        decision.Targets.Should().Equal(SystemName.SystemA);
        decision.Source.Should().Be(RouteSources.DocumentRegistry);
    }

    [Fact]
    public async Task ReceivedTransfer_OnlyTheDebtorIsRegistered_StaysOnSystemA()
    {
        // The debtor is the other bank's customer; only our customer (the creditor) decides.
        _registry.Setup(r => r.Contains("55566677788")).Returns(true);

        var decision = await Router().RouteAsync(
            Message("pacs.008", creditorDocument: "11122233344", debtorDocument: "55566677788"));

        decision.Targets.Should().Equal(SystemName.SystemA);
    }

    [Fact]
    public async Task ReceivedTransfer_TransactionAlreadyBound_IgnoresTheRegistry()
    {
        _affinities.Setup(r => r.FindAsync(new TxKey(TxKeyKind.E2E, EndToEndId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SystemName.SystemA);
        _registry.Setup(r => r.Contains(It.IsAny<string>())).Returns(true);

        var decision = await Router().RouteAsync(
            Message("pacs.008", txKeys: [new TxKey(TxKeyKind.E2E, EndToEndId)], creditorDocument: "11122233344"));

        decision.Targets.Should().Equal(SystemName.SystemA);
        decision.Source.Should().Be(RouteSources.TxAffinity);
    }

    [Fact]
    public async Task ReceivedTransfer_WithoutDocument_GoesToTheDefaultSystem()
    {
        var decision = await Router().RouteAsync(Message("pacs.008"));

        decision.Source.Should().Be(RouteSources.Default);
        _registry.Verify(r => r.Contains(It.IsAny<string>()), Times.Never);
    }

    // ---- transaction bindings -----------------------------------------------------------------

    [Fact]
    public async Task ReceivedTransfer_BindsItsTransactionToTheChosenSystem()
    {
        _registry.Setup(r => r.Contains("11122233344")).Returns(true);
        var key = new TxKey(TxKeyKind.E2E, EndToEndId);

        var decision = await Router().RouteAsync(Message("pacs.008", txKeys: [key], creditorDocument: "11122233344"));

        decision.Bindings.Should().Equal(new TxBinding(key, SystemName.SystemB));
    }

    [Fact]
    public async Task ReportThatMentionsAPayment_DoesNotBindIt()
    {
        // camt.054 goes to whoever asked for it; the payment in it may belong to the other system.
        _sentRoutes.Setup(r => r.FindSystemByMsgIdAsync("M-CAMT060", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SystemName.SystemB);

        var decision = await Router().RouteAsync(
            Message("camt.054", originalRefs: ["M-CAMT060"], txKeys: [new TxKey(TxKeyKind.E2E, EndToEndId)]));

        decision.Targets.Should().Equal(SystemName.SystemB);
        decision.Bindings.Should().BeEmpty();
    }

    // ---- several transactions in one message --------------------------------------------------

    private static ParsedSpiMessage Batch(string msgType, params ParsedTransaction[] transactions) =>
        new(msgType, "M-BATCH", [], transactions, null);

    private static ParsedTransaction Transfer(string endToEndId, string creditorDocument) =>
        new([new TxKey(TxKeyKind.E2E, endToEndId)], creditorDocument, null, null);

    private static ParsedTransaction Status(string endToEndId) =>
        new([new TxKey(TxKeyKind.E2E, endToEndId)], null, null, null);

    [Fact]
    public async Task Batch_AllTransactionsOfOneSystem_IsRoutedLikeASingleOne()
    {
        _registry.Setup(r => r.Contains(It.IsAny<string>())).Returns(true);

        var decision = await Router().RouteAsync(Batch("pacs.008", Transfer("E1", "111"), Transfer("E2", "222")));

        decision.Targets.Should().Equal(SystemName.SystemB);
        decision.Source.Should().Be(RouteSources.DocumentRegistry);
        decision.Bindings.Should().Equal(
            new TxBinding(new TxKey(TxKeyKind.E2E, "E1"), SystemName.SystemB),
            new TxBinding(new TxKey(TxKeyKind.E2E, "E2"), SystemName.SystemB));
    }

    [Fact]
    public async Task Batch_ReceivedTransfersOfBothSystems_IsSplit_EachSystemGettingItsOwnTransfers()
    {
        _registry.Setup(r => r.Contains("222")).Returns(true);

        var decision = await Router().RouteAsync(
            Batch("pacs.008", Transfer("E1", "111"), Transfer("E2", "222"), Transfer("E3", "111")));

        decision.Source.Should().Be(RouteSources.Split);
        decision.Targets.Should().Equal(SystemName.SystemA, SystemName.SystemB);
        decision.Parts.Should().HaveCount(2);
        decision.Parts[0].System.Should().Be(SystemName.SystemA);
        decision.Parts[0].Transactions.Should().Equal(0, 2);
        decision.Parts[1].System.Should().Be(SystemName.SystemB);
        decision.Parts[1].Transactions.Should().Equal(1);
        decision.Bindings.Should().Equal(
            new TxBinding(new TxKey(TxKeyKind.E2E, "E1"), SystemName.SystemA),
            new TxBinding(new TxKey(TxKeyKind.E2E, "E2"), SystemName.SystemB),
            new TxBinding(new TxKey(TxKeyKind.E2E, "E3"), SystemName.SystemA));
    }

    [Fact]
    public async Task Batch_StatusOfPaymentsOfBothSystems_IsSplit()
    {
        _affinities.Setup(r => r.FindAsync(new TxKey(TxKeyKind.E2E, "E1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SystemName.SystemA);
        _affinities.Setup(r => r.FindAsync(new TxKey(TxKeyKind.E2E, "E2"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SystemName.SystemB);

        var decision = await Router().RouteAsync(Batch("pacs.002", Status("E1"), Status("E2")));

        decision.Source.Should().Be(RouteSources.Split);
        decision.Parts.Select(p => (p.System, p.Transactions.Single())).Should().Equal(
            (SystemName.SystemA, 0), (SystemName.SystemB, 1));
    }

    [Fact]
    public async Task Batch_AllTransactionsOfOneSystem_HasNoParts()
    {
        var decision = await Router().RouteAsync(Batch("pacs.008", Transfer("E1", "111"), Transfer("E2", "333")));

        decision.Targets.Should().Equal(SystemName.SystemA);
        decision.Parts.Should().BeEmpty();
    }

    [Fact]
    public async Task Batch_PolicyAllOwners_DeliversTheWholeMessageToEveryOwner()
    {
        _options.Rules["pacs.008"] = new RoutingRule
        {
            Chain = [RouteSources.TxAffinity, RouteSources.DocumentRegistry, RouteSources.Default],
            WhenOwnersDiffer = MixedOwnersPolicy.AllOwners
        };
        _registry.Setup(r => r.Contains("222")).Returns(true);

        var decision = await Router().RouteAsync(Batch("pacs.008", Transfer("E1", "111"), Transfer("E2", "222")));

        decision.Source.Should().Be(RouteSources.Mixed);
        decision.Targets.Should().Equal(SystemName.SystemA, SystemName.SystemB);
        decision.Parts.Should().BeEmpty();
    }

    [Fact]
    public async Task Batch_PolicyDefaultSystem_GivesEverythingToTheDefaultSystemWhichThenOwnsAllOfIt()
    {
        _options.Rules["pacs.008"] = new RoutingRule
        {
            Chain = [RouteSources.TxAffinity, RouteSources.DocumentRegistry, RouteSources.Default],
            WhenOwnersDiffer = MixedOwnersPolicy.DefaultSystem
        };
        _registry.Setup(r => r.Contains("222")).Returns(true);

        var decision = await Router().RouteAsync(Batch("pacs.008", Transfer("E1", "111"), Transfer("E2", "222")));

        decision.Source.Should().Be(RouteSources.Mixed);
        decision.Targets.Should().Equal(SystemName.SystemA);
        decision.Parts.Should().BeEmpty();
        decision.Bindings.Should().OnlyContain(b => b.System == SystemName.SystemA).And.HaveCount(2);
    }

    // ---- messages that travel in both directions ----------------------------------------------

    [Fact]
    public async Task MandateMessage_IsRoutedByTheCustomerOnOurSide_NotByTheCounterparty()
    {
        // The counterparty happens to be a System B customer too; only our side of the mandate counts.
        _registry.Setup(r => r.Contains("COUNTERPARTY")).Returns(true);

        var decision = await Router().RouteAsync(Message(
            "pain.012", creditorDocument: "COUNTERPARTY", debtorDocument: "OURS", ownSideDocument: "OURS"));

        decision.Targets.Should().Equal(SystemName.SystemA);
        decision.Source.Should().Be(RouteSources.DocumentRegistry);
    }

    [Fact]
    public async Task MandateMessage_SideUnknown_FallsToTheDefaultSystem()
    {
        _registry.Setup(r => r.Contains(It.IsAny<string>())).Returns(true);

        var decision = await Router().RouteAsync(Message("pain.011", creditorDocument: "X", debtorDocument: "Y"));

        decision.Source.Should().Be(RouteSources.Default);
    }

    [Fact]
    public async Task Notification_GoesToBothSystems()
    {
        var decision = await Router().RouteAsync(Message("admi.004"));

        decision.Targets.Should().Equal(SystemName.SystemA, SystemName.SystemB);
        decision.Source.Should().Be(RouteSources.Broadcast);
    }

    [Fact]
    public async Task EchoReply_GoesToTheSystemThatSentTheSameContent()
    {
        _sentRoutes.Setup(r => r.ClaimEchoAsync(EchoHash.Compute("ping-b"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SystemName.SystemB);

        var decision = await Router().RouteAsync(Message("pibr.002", echoData: "ping-b"));

        decision.Targets.Should().Equal(SystemName.SystemB);
        decision.Source.Should().Be(RouteSources.EchoData);
    }

    [Fact]
    public async Task Rejection_ReferencingABacenResourceId_GoesToTheSender()
    {
        _sentRoutes.Setup(r => r.FindSystemByPiResourceIdAsync("RESOURCE-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SystemName.SystemB);

        var decision = await Router().RouteAsync(Message("admi.002", originalRefs: ["RESOURCE-1"]));

        decision.Targets.Should().Equal(SystemName.SystemB);
        decision.Source.Should().Be(RouteSources.SentByPiResourceId);
    }

    [Fact]
    public async Task UnknownType_IsDeliveredToBothSystems()
    {
        var decision = await Router().RouteAsync(ParsedSpiMessage.Unparseable);

        decision.Targets.Should().Equal(SystemName.SystemA, SystemName.SystemB);
    }

    [Fact]
    public async Task DefaultSystem_IsConfigurable()
    {
        _options.DefaultSystem = SystemName.SystemB;

        var decision = await Router().RouteAsync(Message("pacs.004"));

        decision.Targets.Should().Equal(SystemName.SystemB);
    }

    [Fact]
    public void Constructor_ChainNamesUnknownResolver_Throws()
    {
        _options.Rules["pacs.008"] = new RoutingRule { Chain = ["NoSuchResolver"] };

        var act = Router;

        act.Should().Throw<InvalidOperationException>().WithMessage("*NoSuchResolver*");
    }
}
