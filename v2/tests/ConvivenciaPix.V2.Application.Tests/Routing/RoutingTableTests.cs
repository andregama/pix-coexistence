using ConvivenciaPix.V2.Application.Routing;
using ConvivenciaPix.V2.Domain.Routing;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ConvivenciaPix.V2.Application.Tests.Routing;

public sealed class RoutingTableTests
{
    private static RoutingTable Table(RoutingOptions? options = null) => new(Options.Create(options ?? new RoutingOptions()));

    [Fact]
    public void For_KnownType_ReturnsItsDefaultChain()
    {
        var rule = Table().For("pacs.008");

        rule.Chain.Should().Equal(RouteSources.TxAffinity, RouteSources.DocumentRegistry, RouteSources.Default);
        rule.DocumentParty.Should().Be(DocumentParty.Creditor);
    }

    [Fact]
    public void For_TypeCoveredOnlyByFamilyWildcard_ReturnsTheFamilyRule() =>
        Table().For("reda.031").Chain.Should().Equal(RouteSources.SentByOriginalMsgId, RouteSources.Broadcast);

    [Fact]
    public void For_UnknownType_FallsBackToBroadcastChain() =>
        Table().For("unknown").Chain.Should().Equal(RouteSources.SentByOriginalMsgId, RouteSources.Broadcast);

    [Fact]
    public void For_ConfiguredRule_ReplacesTheDefault()
    {
        var options = new RoutingOptions();
        options.Rules["pacs.008"] = new RoutingRule { Chain = [RouteSources.Broadcast] };

        Table(options).For("pacs.008").Chain.Should().Equal(RouteSources.Broadcast);
    }

    [Fact]
    public void Constructor_ConfiguredRuleWithEmptyChain_Throws()
    {
        var options = new RoutingOptions();
        options.Rules["pacs.008"] = new RoutingRule();

        var act = () => Table(options);

        act.Should().Throw<InvalidOperationException>().WithMessage("*pacs.008*");
    }
}
