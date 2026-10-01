using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Application.UseCases.PullStream;
using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Domain.Messages;
using ConvivenciaPix.V2.Domain.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using System.Diagnostics;
using Xunit;

namespace ConvivenciaPix.V2.Application.Tests.UseCases;

public sealed class PullStreamUseCaseTests
{
    private static readonly DeliveryItem Item = new("RES-1", "<xml/>"u8.ToArray());

    private readonly Mock<IDeliveryQueue> _queue = new();
    private readonly Mock<IDeliveryNotifier> _notifier = new();

    public PullStreamUseCaseTests()
    {
        _notifier.Setup(n => n.NextNotification(It.IsAny<SystemName>())).Returns(new TaskCompletionSource().Task);
        _queue.Setup(q => q.ClaimAsync(
                It.IsAny<SystemName>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private PullStreamUseCase UseCase(int longPollSeconds = 0) => new(
        _queue.Object, _notifier.Object,
        Options.Create(new DeliveryStreamOptions
        {
            LongPollSeconds = longPollSeconds,
            MaxMessagesPerMultipart = 10,
            LeaseSeconds = 30,
            PollIntervalMs = 50
        }),
        TimeProvider.System);

    [Fact]
    public async Task ContinuingAStream_AcksThePreviousBatchOfThatSystemOnly()
    {
        await UseCase().ExecuteAsync(SystemName.SystemB, "prev-stream", multipart: true);

        _queue.Verify(q => q.AckAsync(SystemName.SystemB, "prev-stream", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartingAStream_AcksNothing()
    {
        await UseCase().ExecuteAsync(SystemName.SystemA, previousStreamId: null, multipart: true);

        _queue.Verify(q => q.AckAsync(It.IsAny<SystemName>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true, 10)]
    [InlineData(false, 1)]
    public async Task LeasesABatchSizedForTheResponseFormat(bool multipart, int expectedMax)
    {
        _queue.Setup(q => q.ClaimAsync(
                SystemName.SystemA, It.IsAny<string>(), expectedMax, TimeSpan.FromSeconds(30), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Item]);

        var result = await UseCase().ExecuteAsync(SystemName.SystemA, null, multipart);

        result.Messages.Should().Equal(Item);
        result.NextStreamId.Should().MatchRegex("^[0-9a-f]{12}$");
    }

    [Fact]
    public async Task EmptyQueue_ReturnsNoMessagesWhenTheLongPollExpires()
    {
        var result = await UseCase(longPollSeconds: 0).ExecuteAsync(SystemName.SystemA, null, multipart: true);

        result.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task MessageArrivingDuringTheLongPoll_IsDeliveredAsSoonAsTheStreamIsNotified()
    {
        var signal = new TaskCompletionSource();
        _notifier.Setup(n => n.NextNotification(SystemName.SystemA)).Returns(signal.Task);

        var claims = 0;
        _queue.Setup(q => q.ClaimAsync(
                It.IsAny<SystemName>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++claims == 1 ? [] : [Item]);

        var options = Options.Create(new DeliveryStreamOptions { LongPollSeconds = 30, PollIntervalMs = 30_000 });
        var useCase = new PullStreamUseCase(_queue.Object, _notifier.Object, options, TimeProvider.System);

        var stopwatch = Stopwatch.StartNew();
        var pull = useCase.ExecuteAsync(SystemName.SystemA, null, multipart: true);
        signal.SetResult();
        var result = await pull;

        result.Messages.Should().Equal(Item);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }
}
