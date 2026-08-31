using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Infrastructure.Persistence.Repositories;
using ConvivenciaPix.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Persistence;

public sealed class SpiReceivedMsgRepositoryTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public SpiReceivedMsgRepositoryTests(SqlServerFixture fixture) => _fixture = fixture;

    private SpiReceivedMsgRepository CreateRepo() => new(_fixture.CreateDbContext());

    [Fact]
    public async Task PiResourceId_PersistsThroughUpdate()
    {
        var id = Uid();
        var msg = SpiReceivedMsg.CreateFromSystemA(id, "pacs.008", msgId: null, "<a/>", errorCode: null);
        msg.SetSystemBXml("<b/>");
        msg.SetPiResourceId("rid-" + id);
        await CreateRepo().AddAsync(msg);

        var found = await CreateRepo().FindByIdempotentIdAsync(id);
        found!.PiResourceId.Should().Be("rid-" + id);
        found.ConsumedAt.Should().BeNull();
    }

    [Fact]
    public async Task MarkConsumedByResourceIds_SetsConsumedAt_OnlyForMatchingRows()
    {
        var consumedId = Uid();
        var untouchedId = Uid();
        var rid = "rid-" + consumedId;

        var consumed = SpiReceivedMsg.CreateFromSystemA(consumedId, "pacs.008", null, "<a/>", null);
        consumed.SetSystemBXml("<b/>");
        consumed.SetPiResourceId(rid);

        var untouched = SpiReceivedMsg.CreateFromSystemA(untouchedId, "pacs.008", null, "<a/>", null);
        untouched.SetSystemBXml("<b/>");
        untouched.SetPiResourceId("rid-" + untouchedId);

        var seedRepo = CreateRepo();
        await seedRepo.AddAsync(consumed);
        await seedRepo.AddAsync(untouched);

        var marked = await CreateRepo().MarkConsumedByResourceIdsAsync(new[] { rid }, DateTime.UtcNow);

        marked.Should().Be(1);
        (await CreateRepo().FindByIdempotentIdAsync(consumedId))!.ConsumedAt.Should().NotBeNull();
        (await CreateRepo().FindByIdempotentIdAsync(untouchedId))!.ConsumedAt.Should().BeNull();
    }

    [Fact]
    public async Task MarkConsumedByResourceIds_IsFirstWins_DoesNotReMarkAlreadyConsumed()
    {
        var id = Uid();
        var rid = "rid-" + id;
        var msg = SpiReceivedMsg.CreateFromSystemA(id, "pacs.008", null, "<a/>", null);
        msg.SetSystemBXml("<b/>");
        msg.SetPiResourceId(rid);
        await CreateRepo().AddAsync(msg);

        var first = await CreateRepo().MarkConsumedByResourceIdsAsync(new[] { rid }, DateTime.UtcNow);
        var second = await CreateRepo().MarkConsumedByResourceIdsAsync(new[] { rid }, DateTime.UtcNow);

        first.Should().Be(1);
        second.Should().Be(0); // already consumed → untouched
    }

    [Fact]
    public async Task MarkConsumedByResourceIds_EmptyList_ReturnsZero()
    {
        var marked = await CreateRepo().MarkConsumedByResourceIdsAsync(Array.Empty<string>(), DateTime.UtcNow);
        marked.Should().Be(0);
    }

    private static string Uid() => Guid.NewGuid().ToString("N")[..16];
}
