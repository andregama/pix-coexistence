using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Infrastructure.Notifications;
using FluentAssertions;
using Xunit;

namespace ConvivenciaPix.V2.Infrastructure.Tests.Notifications;

public sealed class InMemoryDeliveryNotifierTests
{
    [Fact]
    public async Task Notify_CompletesWaitersOfThatSystemOnly()
    {
        var notifier = new InMemoryDeliveryNotifier();
        var waitingA = notifier.NextNotification(SystemName.SystemA);
        var waitingB = notifier.NextNotification(SystemName.SystemB);

        await notifier.NotifyAsync(SystemName.SystemB);

        waitingB.IsCompleted.Should().BeTrue();
        waitingA.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task NextNotification_AfterANotify_WaitsForTheNextOne()
    {
        var notifier = new InMemoryDeliveryNotifier();
        await notifier.NotifyAsync(SystemName.SystemA);

        notifier.NextNotification(SystemName.SystemA).IsCompleted.Should().BeFalse();
    }
}
