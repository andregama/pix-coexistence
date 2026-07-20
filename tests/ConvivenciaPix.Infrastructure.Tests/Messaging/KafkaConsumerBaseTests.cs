using Confluent.Kafka;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Infrastructure.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Messaging;

/// <summary>
/// Guards the offset-commit contract of <see cref="KafkaConsumerBase{TKey,TValue}"/>: consumers run
/// with EnableAutoCommit=false, so the base loop must commit each message's offset after it is
/// processed (or dead-lettered). Without this, a pod restart replays every retained message and
/// creates duplicate responses.
/// </summary>
public sealed class KafkaConsumerBaseTests
{
    private const string Topic = "test-topic";

    // Consume() returns the message once, then cancels the loop's token and throws so ExecuteAsync
    // exits cleanly through its OperationCanceledException guard.
    private static Mock<IConsumer<string, string>> ConsumerReturningOnce(
        ConsumeResult<string, string> result, CancellationTokenSource cts)
    {
        var consumer = new Mock<IConsumer<string, string>>();
        var calls = 0;
        consumer.Setup(c => c.Consume(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    return result;
                cts.Cancel();
                throw new OperationCanceledException();
            });
        return consumer;
    }

    private static ConsumeResult<string, string> Message(long offset) => new()
    {
        Topic = Topic,
        Partition = new Partition(0),
        Offset = new Offset(offset),
        Message = new Message<string, string> { Key = "k", Value = "v", Headers = new Headers() },
    };

    [Fact]
    public async Task ProcessedMessage_IsOffsetCommitted()
    {
        var result = Message(offset: 42);
        using var cts = new CancellationTokenSource();
        var consumer = ConsumerReturningOnce(result, cts);
        var dlq = new Mock<IProducer<string, string>>();

        var sut = new TestConsumer(consumer.Object, dlq.Object, _ => Task.CompletedTask);
        await sut.RunAsync(cts.Token);

        consumer.Verify(c => c.Commit(result), Times.Once,
            "a successfully processed message must have its offset committed");
        dlq.Verify(p => p.ProduceAsync(It.IsAny<string>(), It.IsAny<Message<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FailedMessage_IsDeadLettered_AndOffsetCommitted()
    {
        var result = Message(offset: 7);
        using var cts = new CancellationTokenSource();
        var consumer = ConsumerReturningOnce(result, cts);
        var dlq = new Mock<IProducer<string, string>>();
        dlq.Setup(p => p.ProduceAsync(It.IsAny<string>(), It.IsAny<Message<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeliveryResult<string, string>());

        var metrics = new Mock<ISpiMetrics>();
        var sut = new TestConsumer(consumer.Object, dlq.Object,
            _ => throw new InvalidOperationException("boom"), metrics.Object);
        await sut.RunAsync(cts.Token);

        dlq.Verify(p => p.ProduceAsync(Topics.DlqFor(Topic), It.IsAny<Message<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Once, "a failed message must be dead-lettered");
        consumer.Verify(c => c.Commit(result), Times.Once,
            "the offset must advance past a dead-lettered message so it is not reprocessed on restart");
    }

    [Fact]
    public async Task CommitFailure_IsSwallowed_AndLoopExitsCleanly()
    {
        var result = Message(offset: 99);
        using var cts = new CancellationTokenSource();
        var consumer = ConsumerReturningOnce(result, cts);
        consumer.Setup(c => c.Commit(It.IsAny<ConsumeResult<string, string>>()))
            .Throws(new KafkaException(ErrorCode.Local_AllBrokersDown));
        var dlq = new Mock<IProducer<string, string>>();

        var sut = new TestConsumer(consumer.Object, dlq.Object, _ => Task.CompletedTask);

        // A commit failure must not escape the loop; the consumer still closes on shutdown.
        await sut.RunAsync(cts.Token);

        consumer.Verify(c => c.Close(), Times.Once);
    }

    private sealed class TestConsumer : KafkaConsumerBase<string, string>
    {
        private readonly Func<ConsumeResult<string, string>, Task> _handler;

        public TestConsumer(
            IConsumer<string, string> consumer,
            IProducer<string, string> dlqProducer,
            Func<ConsumeResult<string, string>, Task> handler,
            ISpiMetrics? metrics = null)
            : base(consumer, dlqProducer, Topic, NullLogger.Instance, metrics ?? Mock.Of<ISpiMetrics>())
        {
            _handler = handler;
        }

        protected override Task ProcessMessageAsync(
            ConsumeResult<string, string> result, CancellationToken cancellationToken) => _handler(result);

        public Task RunAsync(CancellationToken cancellationToken) => ExecuteAsync(cancellationToken);
    }
}
