using ConvivenciaPix.Infrastructure.Cache;
using ConvivenciaPix.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConvivenciaPix.Infrastructure.Tests.Cache;

public sealed class RedisResponseCacheTests : IClassFixture<RedisFixture>
{
    private readonly RedisResponseCache _cache;

    public RedisResponseCacheTests(RedisFixture fixture)
    {
        _cache = new RedisResponseCache(fixture.Connection, NullLogger<RedisResponseCache>.Instance);
    }

    private static string UniqueKey() => Guid.NewGuid().ToString();

    [Fact]
    public async Task SetAsync_ThenGetAsync_ReturnsStoredValue()
    {
        var key = UniqueKey();
        await _cache.SetAsync(key, "response-xml", TimeSpan.FromMinutes(5));
        var result = await _cache.GetAsync(key);
        result.Should().Be("response-xml");
    }

    [Fact]
    public async Task GetAsync_UnknownKey_ReturnsNull()
    {
        var result = await _cache.GetAsync(UniqueKey());
        result.Should().BeNull();
    }

    [Fact]
    public async Task SetIdempotencyKeyAsync_FirstWrite_Stores()
    {
        var msgId = UniqueKey();
        await _cache.SetIdempotencyKeyAsync(msgId, "first-response", TimeSpan.FromHours(1));
        var result = await _cache.GetIdempotencyKeyAsync(msgId);
        result.Should().Be("first-response");
    }

    [Fact]
    public async Task SetIdempotencyKeyAsync_SecondWrite_DoesNotOverwrite()
    {
        var msgId = UniqueKey();
        await _cache.SetIdempotencyKeyAsync(msgId, "first-response", TimeSpan.FromHours(1));
        await _cache.SetIdempotencyKeyAsync(msgId, "second-response", TimeSpan.FromHours(1));
        var result = await _cache.GetIdempotencyKeyAsync(msgId);
        result.Should().Be("first-response");
    }

    [Fact]
    public async Task GetIdempotencyKeyAsync_UnknownKey_ReturnsNull()
    {
        var result = await _cache.GetIdempotencyKeyAsync(UniqueKey());
        result.Should().BeNull();
    }
}
