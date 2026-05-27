using ConvivenciaPix.Infrastructure.Outbound;
using ConvivenciaPix.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Diagnostics;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Outbound;

public sealed class RedisOutboundStreamTests : IClassFixture<RedisFixture>
{
    private readonly RedisFixture _fixture;
    private readonly RedisOutboundStream _stream;

    public RedisOutboundStreamTests(RedisFixture fixture)
    {
        _fixture = fixture;
        ClearQueue(fixture.Connection);
        var options = Options.Create(new OutboundStreamOptions { LongPollSeconds = 1, StreamTtlMinutes = 10 });
        _stream = new RedisOutboundStream(fixture.Connection, options, NullLogger<RedisOutboundStream>.Instance);
    }

    private static void ClearQueue(IConnectionMultiplexer redis)
    {
        var db = redis.GetDatabase();
        db.KeyDelete("spi:out:queue");
    }

    [Fact]
    public async Task Enqueue_ThenPeek_ReturnsTheMessage()
    {
        await _stream.EnqueueAsync("rid-1", "<signed/>");
        var messages = await _stream.PeekAsync(10, TimeSpan.FromSeconds(1), CancellationToken.None);
        messages.Should().ContainSingle(m => m.PiResourceId == "rid-1" && m.SignedXml == "<signed/>");
    }

    [Fact]
    public async Task PeekEmpty_LongPolls_ThenReturnsEmpty()
    {
        ClearQueue(_fixture.Connection);
        var sw = Stopwatch.StartNew();
        var messages = await _stream.PeekAsync(10, TimeSpan.FromMilliseconds(500), CancellationToken.None);
        sw.Stop();
        messages.Should().BeEmpty();
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(400);
    }

    [Fact]
    public async Task LongPoll_WokenByEnqueue()
    {
        ClearQueue(_fixture.Connection);
        var sw = Stopwatch.StartNew();
        var peekTask = _stream.PeekAsync(10, TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(100);
        await _stream.EnqueueAsync("rid-wake", "<late/>");
        var messages = await peekTask;
        sw.Stop();
        messages.Should().ContainSingle(m => m.PiResourceId == "rid-wake");
        sw.ElapsedMilliseconds.Should().BeLessThan(3000);
    }

    [Fact]
    public async Task SaveAndGetAndDelete_RoundTripsStream()
    {
        await _stream.SaveStreamAsync("stream-1", new[] { "rid-1", "rid-2" });
        var first = await _stream.GetAndDeleteStreamAsync("stream-1");
        first.Should().NotBeNull();
        first!.Should().Equal("rid-1", "rid-2");

        var second = await _stream.GetAndDeleteStreamAsync("stream-1");
        second.Should().BeNull();
    }

    [Fact]
    public async Task Commit_RemovesMessagesFromQueue()
    {
        ClearQueue(_fixture.Connection);
        await _stream.EnqueueAsync("rid-1", "<a/>");
        await _stream.EnqueueAsync("rid-2", "<b/>");
        await _stream.CommitAsync(new[] { "rid-1" });

        var remaining = await _stream.PeekAsync(10, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        remaining.Should().ContainSingle(m => m.PiResourceId == "rid-2");
    }
}
