using ConvivenciaPix.Infrastructure.Messaging.Dlq;
using FluentAssertions;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Messaging;

public sealed class DlqRetryPolicyTests
{
    private static readonly DateTimeOffset DeadLetteredAt = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static DlqRetryPolicy Policy() => new(new DlqRetryOptions
    {
        MaxRetries = 5,
        InitialDelaySeconds = 30,
        BackoffMultiplier = 2.0,
        MaxDelaySeconds = 300,
    });

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 240)]
    [InlineData(4, 300)] // 480s capped at MaxDelaySeconds
    public void Decide_Retries_WithExponentialBackoff_FromDeadLetterTime(int retryCount, int expectedDelaySeconds)
    {
        var decision = Policy().Decide(retryCount, "System.InvalidOperationException", DeadLetteredAt);

        decision.Retry.Should().BeTrue();
        decision.DueAt.Should().Be(DeadLetteredAt.AddSeconds(expectedDelaySeconds));
    }

    [Fact]
    public void Decide_GivesUp_WhenMaxRetriesReached()
    {
        var decision = Policy().Decide(5, "System.InvalidOperationException", DeadLetteredAt);

        decision.Retry.Should().BeFalse();
        decision.Reason.Should().Contain("exhausted");
    }

    [Fact]
    public void Decide_GivesUp_OnNonRetryableExceptionType()
    {
        var decision = Policy().Decide(0, "System.Text.Json.JsonException", DeadLetteredAt);

        decision.Retry.Should().BeFalse();
        decision.Reason.Should().Contain("non-retryable");
    }

    [Fact]
    public void Decide_Retries_WhenExceptionTypeIsUnknown()
    {
        Policy().Decide(0, null, DeadLetteredAt).Retry.Should().BeTrue();
    }
}
