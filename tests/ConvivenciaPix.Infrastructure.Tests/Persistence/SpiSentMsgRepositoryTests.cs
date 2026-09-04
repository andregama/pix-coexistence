using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Infrastructure.Persistence.Repositories;
using ConvivenciaPix.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Persistence;

public sealed class SpiSentMsgRepositoryTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public SpiSentMsgRepositoryTests(SqlServerFixture fixture) => _fixture = fixture;

    private SpiSentMsgRepository CreateRepo() => new(_fixture.CreateDbContext());

    [Fact]
    public async Task AddAsync_ThenFindByIdempotentId_ReturnsEntity()
    {
        var idempotentId = Uid();
        var msg = SpiSentMsg.Create(idempotentId, "pacs.008");
        var repo = CreateRepo();

        await repo.AddAsync(msg);
        var found = await repo.FindByIdempotentIdAsync(idempotentId);

        found.Should().NotBeNull();
        found!.IdempotentId.Should().Be(idempotentId);
        found.MsgType.Should().Be("pacs.008");
    }

    [Fact]
    public async Task FindByIdempotentId_UnknownId_ReturnsNull()
    {
        var result = await CreateRepo().FindByIdempotentIdAsync(Uid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task FindByMsgIdSystemA_ReturnsRow_WhenMatchingMsgIdExists()
    {
        var idempotentId = Uid();
        var msgIdA = "MSGA-" + idempotentId;
        var msg = SpiSentMsg.Create(idempotentId, "pacs.008");
        msg.UpdateFromSystemA(msgIdA, "<xmlA/>", null);
        await CreateRepo().AddAsync(msg);

        var found = await CreateRepo().FindByMsgIdSystemAAsync(msgIdA);

        found.Should().NotBeNull();
        found!.IdempotentId.Should().Be(idempotentId);
        found.MsgIdSystemA.Should().Be(msgIdA);
    }

    [Fact]
    public async Task FindByMsgIdSystemA_UnknownMsgId_ReturnsNull()
    {
        (await CreateRepo().FindByMsgIdSystemAAsync("MSGA-" + Uid())).Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_PersistsChanges()
    {
        var idempotentId = Uid();
        var msg = SpiSentMsg.Create(idempotentId, "pacs.008");
        await CreateRepo().AddAsync(msg);

        var repo = CreateRepo();
        var found = await repo.FindByIdempotentIdAsync(idempotentId);
        found!.UpdateFromSystemA("MSG-A", "<xmlA/>", null);
        await repo.UpdateAsync(found);

        var updated = await CreateRepo().FindByIdempotentIdAsync(idempotentId);
        updated!.MsgIdSystemA.Should().Be("MSG-A");
        updated.XmlMsgSystemA.Should().Be("<xmlA/>");
    }

    [Fact]
    public async Task AddAsync_PersistsCorrelationSource()
    {
        var idempotentId = Uid();
        var msg = SpiSentMsg.Create(idempotentId, "pain.012");
        msg.SetCorrelationSource("DerivedKey");
        await CreateRepo().AddAsync(msg);

        var found = await CreateRepo().FindByIdempotentIdAsync(idempotentId);
        found!.CorrelationSource.Should().Be("DerivedKey");
    }

    [Fact]
    public async Task DeleteOlderThan_RemovesStaleRows_LeavesRecentRows()
    {
        var staleId = Uid();
        var recentId = Uid();
        var stale = SpiSentMsg.Create(staleId, "pacs.008");
        var recent = SpiSentMsg.Create(recentId, "pacs.008");

        await using var ctx = _fixture.CreateDbContext();
        ctx.SpiSentMsgs.Add(stale);
        ctx.SpiSentMsgs.Add(recent);
        await ctx.SaveChangesAsync();

        await ctx.Database.ExecuteSqlRawAsync(
            "UPDATE SpiSentMsg SET CreatedAt = {0} WHERE IdempotentId = {1}",
            DateTime.UtcNow.AddDays(-40), staleId);

        var repo = new SpiSentMsgRepository(ctx);
        var deleted = await repo.DeleteOlderThanAsync(DateTime.UtcNow.AddDays(-30));

        deleted.Should().BeGreaterThanOrEqualTo(1);
        (await repo.FindByIdempotentIdAsync(staleId)).Should().BeNull();
        (await repo.FindByIdempotentIdAsync(recentId)).Should().NotBeNull();
    }

    [Fact]
    public async Task UpsertSystemA_InsertsWhenMissing_ThenSystemB_UpdatesAndPreservesA()
    {
        var id = Uid();

        var a = SpiSentMsg.Create(id, "pacs.008");
        a.UpdateFromSystemA("MSGA-" + id, "<a/>", null);
        a.SetCorrelationSource("MessageKey");
        var insertA = await CreateRepo().UpsertSystemAAsync(a);
        insertA.Inserted.Should().BeTrue();

        var b = SpiSentMsg.Create(id, "pacs.008");
        b.UpdateFromSystemB("MSGB-" + id, "<b/>", null);
        b.SetCorrelationSource("DerivedKey"); // must NOT overwrite A's first-wins value
        var upsertB = await CreateRepo().UpsertSystemBAsync(b);
        upsertB.Inserted.Should().BeFalse();

        var row = await CreateRepo().FindByIdempotentIdAsync(id);
        row!.XmlMsgSystemA.Should().Be("<a/>");   // A preserved
        row.XmlMsgSystemB.Should().Be("<b/>");     // B written
        row.IsComplete.Should().BeTrue();
        row.CorrelationSource.Should().Be("MessageKey"); // first-wins
    }

    [Fact]
    public async Task UpsertSystemA_Twice_UpdatesNotInserts()
    {
        var id = Uid();
        var first = SpiSentMsg.Create(id, "pacs.008");
        first.UpdateFromSystemA("MSGA-1", "<a1/>", null);
        (await CreateRepo().UpsertSystemAAsync(first)).Inserted.Should().BeTrue();

        var second = SpiSentMsg.Create(id, "pacs.008");
        second.UpdateFromSystemA("MSGA-2", "<a2/>", null);
        (await CreateRepo().UpsertSystemAAsync(second)).Inserted.Should().BeFalse();

        (await CreateRepo().FindByIdempotentIdAsync(id))!.XmlMsgSystemA.Should().Be("<a2/>");
    }

    [Fact]
    public async Task Upsert_ConcurrentAAndB_ProduceOneCompleteRow_WithoutDuplicateKey()
    {
        var id = Uid();

        var a = SpiSentMsg.Create(id, "pacs.008");
        a.UpdateFromSystemA("MSGA-" + id, "<a/>", null);
        a.SetCorrelationSource("MessageKey");

        var b = SpiSentMsg.Create(id, "pacs.008");
        b.UpdateFromSystemB("MSGB-" + id, "<b/>", null);
        b.SetCorrelationSource("MessageKey");

        // Separate DbContexts/repositories, run in parallel — reproduces the A/B insert race.
        var act = () => Task.WhenAll(
            CreateRepo().UpsertSystemAAsync(a),
            CreateRepo().UpsertSystemBAsync(b));
        await act.Should().NotThrowAsync();

        var row = await CreateRepo().FindByIdempotentIdAsync(id);
        row.Should().NotBeNull();
        row!.XmlMsgSystemA.Should().Be("<a/>");
        row.XmlMsgSystemB.Should().Be("<b/>");
        row.IsComplete.Should().BeTrue();
    }

    private static string Uid() => Guid.NewGuid().ToString("N")[..16];
}
