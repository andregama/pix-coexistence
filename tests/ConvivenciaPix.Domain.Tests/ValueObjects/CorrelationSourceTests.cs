using ConvivenciaPix.Domain.ValueObjects;
using FluentAssertions;

namespace ConvivenciaPix.Domain.Tests.ValueObjects;

public sealed class CorrelationSourceTests
{
    [Fact]
    public void From_Orchestrator_ReturnsSameStaticInstance()
    {
        CorrelationSource.From("Orchestrator").Should().BeSameAs(CorrelationSource.Orchestrator);
    }

    [Fact]
    public void From_Heuristic_ReturnsSameStaticInstance()
    {
        CorrelationSource.From("Heuristic").Should().BeSameAs(CorrelationSource.Heuristic);
    }

    [Theory]
    [InlineData("")]
    [InlineData("orchestrator")]
    [InlineData("HEURISTIC")]
    [InlineData("Unknown")]
    public void From_InvalidValue_ThrowsArgumentException(string invalid)
    {
        var act = () => CorrelationSource.From(invalid);
        act.Should().Throw<ArgumentException>().WithMessage("*Invalid CorrelationSource*");
    }

    [Fact]
    public void Equals_SameValue_ReturnsTrue()
    {
        CorrelationSource.Orchestrator.Should().Be(CorrelationSource.From("Orchestrator"));
    }

    [Fact]
    public void Equals_DifferentValue_ReturnsFalse()
    {
        CorrelationSource.Orchestrator.Should().NotBe(CorrelationSource.Heuristic);
    }

    [Fact]
    public void OrchestratorAndHeuristic_AreNotEqual()
    {
        CorrelationSource.Orchestrator.Equals(CorrelationSource.Heuristic).Should().BeFalse();
    }

    [Fact]
    public void ImplicitStringConversion_ReturnsValue()
    {
        string value = CorrelationSource.Orchestrator;
        value.Should().Be("Orchestrator");
    }

    [Fact]
    public void GetHashCode_SameValue_SameHash()
    {
        CorrelationSource.From("Orchestrator").GetHashCode()
            .Should().Be(CorrelationSource.Orchestrator.GetHashCode());
    }

    [Fact]
    public void ToString_ReturnsValue()
    {
        CorrelationSource.Heuristic.ToString().Should().Be("Heuristic");
    }
}
