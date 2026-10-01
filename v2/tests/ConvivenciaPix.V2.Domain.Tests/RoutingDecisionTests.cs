using ConvivenciaPix.V2.Domain.Routing;
using FluentAssertions;
using Xunit;

namespace ConvivenciaPix.V2.Domain.Tests;

public sealed class RoutingDecisionTests
{
    [Fact]
    public void To_TargetsASingleSystem()
    {
        var decision = RoutingDecision.To(SystemName.SystemB, RouteSources.DocumentRegistry);

        decision.Targets.Should().Equal(SystemName.SystemB);
        decision.TargetsText.Should().Be("SystemB");
        decision.Source.Should().Be(RouteSources.DocumentRegistry);
    }

    [Fact]
    public void ToBoth_TargetsBothSystems()
    {
        var decision = RoutingDecision.ToBoth(RouteSources.Broadcast);

        decision.Targets.Should().Equal(SystemName.SystemA, SystemName.SystemB);
        decision.TargetsText.Should().Be("SystemA,SystemB");
    }
}
