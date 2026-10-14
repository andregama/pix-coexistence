using Confluent.Kafka;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Infrastructure.Messaging.Dlq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Text;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Messaging;

/// <summary>
/// Guards the contract of <see cref="DlqRetryConsumer"/>: a dead-lettered message goes back to its
/// source topic with an incremented retry counter, is given up on once the limit is reached, and is
/// never committed past when it could not be republished.
/// </summary>
public sealed class DlqRetryConsumerTests
{
    private const string DlqTopic = "spi.systema.cdc.dlq";
    private const string SourceTopic = "spi.systema.cdc";

    private readonly Mock<IProducer<byte[], byte[]>> _producer = new();
    private readonly Mock<ISpiMetrics> _metrics = new();
    private readonly List<(string Topic, Message<byte[], byte[]> Message)> _produced = [];

    public DlqRetryConsumerTests()
    {
        _producer.Setup(p => p.ProduceAsync(It.IsAny<string>(), It.IsAny<Message<byte[], byte[]>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Message<byte[], byte[]>, CancellationToken>((t, m, _) => _produced.Add((t, m)))
            .ReturnsAsync(new DeliveryResult<byte[], byte[]>());
    }

    // Consume() returns the message `times` times, then cancels the loop's token and throws so
    // ExecuteAsync exits through its OperationCanceledException guard.
    private static Mock<IConsumer<byte[], byte[]>> ConsumerReturning(
        ConsumeResult<byte[], byte[]> result, CancellationTokenSource cts, int times = 1)
    {
        var consumer = new Mock<IConsumer<byte[], byte[]>>();
        var calls = 0;
        consumer.Setup(c => c.Consume(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref calls) <= times)
                    return result;
                cts.Cancel();
                throw new OperationCanceledException();
            });
        return consumer;
    }

    // Dead-lettered an hour ago, so every backoff has already elapsed and no test waits.
    private static ConsumeResult<byte[], byte[]> DeadLetter(
        int? retryCount = null, string exceptionType = "System.InvalidOperationException", bool withDlqHeaders = true)
    {
        var headers = new Headers { { "correlation-id", Encoding.UTF8.GetBytes("abc") } };
        if (withDlqHeaders)
        {
            headers.Add(DlqHeaders.SourceTopic, Encoding.UTF8.GetBytes(SourceTopic));
            headers.Add(DlqHeaders.ExceptionType, Encoding.UTF8.GetBytes(exceptionType));
            headers.Add(DlqHeaders.ExceptionMessage, Encoding.UTF8.GetBytes("boom"));
            headers.Add(DlqHeaders.Timestamp,
                Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.AddHours(-1).ToString("O")));
        }
        if (retryCount is not null)
            headers.Add(DlqHeaders.RetryCount, DlqHeaders.EncodeRetryCount(retryCount.Value));

        return new ConsumeResult<byte[], byte[]>
        {
            Topic = DlqTopic,
            Partition = new Partition(0),
            Offset = new Offset(11),
            Message = new Message<byte[], byte[]>
            {
                Key = Encoding.UTF8.GetBytes("k"),
                Value = Encoding.UTF8.GetBytes("v"),
                Headers = headers,
                Timestamp = new Timestamp(DateTime.UtcNow.AddHours(-1)),
            },
        };
    }

    private async Task RunAsync(IConsumer<byte[], byte[]> consumer, CancellationToken ct)
    {
        var sut = new DlqRetryConsumer(
            consumer,
            _producer.Object,
            Options.Create(new DlqRetryOptions { MaxRetries = 3, RepublishFailureDelaySeconds = 0 }),
            _metrics.Object,
            TimeProvider.System,
            NullLogger<DlqRetryConsumer>.Instance);

        await sut.StartAsync(ct);
        await sut.ExecuteTask!;
    }

    [Fact]
    public async Task DeadLetter_IsRepublishedToSourceTopic_WithRetryCount_AndCommitted()
    {
        var result = DeadLetter();
        using var cts = new CancellationTokenSource();
        var consumer = ConsumerReturning(result, cts);

        await RunAsync(consumer.Object, cts.Token);

        _produced.Should().ContainSingle();
        var (topic, message) = _produced[0];
        topic.Should().Be(SourceTopic);
        message.Key.Should().Equal(result.Message.Key);
        message.Value.Should().Equal(result.Message.Value);
        DlqHeaders.GetRetryCount(message.Headers).Should().Be(1);
        DlqHeaders.GetString(message.Headers, "correlation-id").Should().Be("abc");
        message.Headers.Select(h => h.Key).Should().NotContain(
            [DlqHeaders.ExceptionType, DlqHeaders.ExceptionMessage, DlqHeaders.SourceTopic, DlqHeaders.Timestamp]);

        consumer.Verify(c => c.Commit(result), Times.Once);
        _metrics.Verify(m => m.RecordDlqRetry(DlqTopic, DlqRetryConsumer.OutcomeRetried), Times.Once);
    }

    [Fact]
    public async Task DeadLetter_AlreadyRetried_IncrementsRetryCount()
    {
        using var cts = new CancellationTokenSource();
        var consumer = ConsumerReturning(DeadLetter(retryCount: 2), cts);

        await RunAsync(consumer.Object, cts.Token);

        DlqHeaders.GetRetryCount(_produced.Should().ContainSingle().Subject.Message.Headers).Should().Be(3);
    }

    [Fact]
    public async Task DeadLetter_WithoutDlqHeaders_GoesToTopicDerivedFromDlqName()
    {
        using var cts = new CancellationTokenSource();
        var consumer = ConsumerReturning(DeadLetter(withDlqHeaders: false), cts);

        await RunAsync(consumer.Object, cts.Token);

        _produced.Should().ContainSingle().Which.Topic.Should().Be(SourceTopic);
    }

    [Fact]
    public async Task DeadLetter_AtMaxRetries_IsNotRepublished_ButCountedAndCommitted()
    {
        var result = DeadLetter(retryCount: 3);
        using var cts = new CancellationTokenSource();
        var consumer = ConsumerReturning(result, cts);

        await RunAsync(consumer.Object, cts.Token);

        _produced.Should().BeEmpty();
        consumer.Verify(c => c.Commit(result), Times.Once);
        _metrics.Verify(m => m.RecordDlqRetry(DlqTopic, DlqRetryConsumer.OutcomeExhausted), Times.Once);
    }

    [Fact]
    public async Task DeadLetter_WithNonRetryableException_IsNotRepublished()
    {
        var result = DeadLetter(exceptionType: "System.Text.Json.JsonException");
        using var cts = new CancellationTokenSource();
        var consumer = ConsumerReturning(result, cts);

        await RunAsync(consumer.Object, cts.Token);

        _produced.Should().BeEmpty();
        consumer.Verify(c => c.Commit(result), Times.Once);
        _metrics.Verify(m => m.RecordDlqRetry(DlqTopic, DlqRetryConsumer.OutcomeExhausted), Times.Once);
    }

    [Fact]
    public async Task RepublishFailure_DoesNotCommit_RewindsAndTriesAgain()
    {
        var result = DeadLetter();
        using var cts = new CancellationTokenSource();
        var consumer = ConsumerReturning(result, cts, times: 2);

        _producer.SetupSequence(p => p.ProduceAsync(It.IsAny<string>(), It.IsAny<Message<byte[], byte[]>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KafkaException(ErrorCode.Local_AllBrokersDown))
            .ReturnsAsync(new DeliveryResult<byte[], byte[]>());

        await RunAsync(consumer.Object, cts.Token);

        consumer.Verify(c => c.Seek(result.TopicPartitionOffset), Times.Once,
            "a message that could not be republished must be read again, not skipped");
        consumer.Verify(c => c.Commit(result), Times.Once, "only the successful second attempt commits");
        _metrics.Verify(m => m.RecordDlqRetry(DlqTopic, DlqRetryConsumer.OutcomeRetried), Times.Once);
    }
}
