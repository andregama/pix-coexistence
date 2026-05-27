using ConvivenciaPix.Infrastructure.Cache;
using ConvivenciaPix.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

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
}
