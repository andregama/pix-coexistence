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

    [Fact]
    public async Task UpsertSystemA_InsertsWhenMissing_ThenSystemB_UpdatesAndPreservesA_NotConsumedAt()
    {
        var id = Uid();

        var a = SpiReceivedMsg.CreateFromSystemA(id, "pacs.002", "MSG-" + id, "<a/>", null);
        a.SetCorrelationSource("MessageKey");
        (await CreateRepo().UpsertSystemAAsync(a)).Inserted.Should().BeTrue();

        var b = SpiReceivedMsg.CreateFromSystemB(id, "pacs.002", msgId: "OTHER", "<b/>", null);
        b.SetPiResourceId("rid-" + id);
        (await CreateRepo().UpsertSystemBAsync(b)).Inserted.Should().BeFalse();

        var row = await CreateRepo().FindByIdempotentIdAsync(id);
        row!.XmlMsgSystemA.Should().Be("<a/>");
        row.XmlMsgSystemB.Should().Be("<b/>");
        row.MsgId.Should().Be("MSG-" + id);        // first-wins (A's MsgId, not B's "OTHER")
        row.PiResourceId.Should().Be("rid-" + id);
        row.ConsumedAt.Should().BeNull();          // never touched by the upsert
        row.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task Upsert_ConcurrentAAndB_ProduceOneCompleteRow_WithoutDuplicateKey()
    {
        var id = Uid();
        var a = SpiReceivedMsg.CreateFromSystemA(id, "pacs.002", "MSG-" + id, "<a/>", null);
        a.SetCorrelationSource("MessageKey");
        var b = SpiReceivedMsg.CreateFromSystemB(id, "pacs.002", msgId: null, "<b/>", null);
        b.SetPiResourceId("rid-" + id);

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

    [Fact]
    public async Task Upsert_SameIdDifferentMsgType_ProducesTwoRows_NoOverwrite()
    {
        // A pacs.008 credit and a pacs.002 response can share an EndToEndId; under the composite key
        // (IdempotentId, MsgType) they are distinct rows — the response must not clobber the credit.
        var id = Uid();

        var credit = SpiReceivedMsg.CreateFromSystemA(id, "pacs.008", "MSG-" + id, "<pacs008/>", errorCode: null);
        credit.SetAmounts(123.45m, 0m);
        (await CreateRepo().UpsertSystemAAsync(credit)).Inserted.Should().BeTrue();

        var response = SpiReceivedMsg.CreateFromSystemA(id, "pacs.002", null, "<pacs002/>", errorCode: null);
        response.SetTxStatus("ACSP");
        (await CreateRepo().UpsertSystemAAsync(response)).Inserted.Should().BeTrue(); // separate row, not an update

        await using var ctx = _fixture.CreateDbContext();
        var rows = ctx.SpiReceivedMsgs
            .Where(x => x.IdempotentId == id)
            .OrderBy(x => x.MsgType)
            .ToList();

        rows.Should().HaveCount(2);
        var creditRow = rows.Single(r => r.MsgType == "pacs.008");
        creditRow.XmlMsgSystemA.Should().Be("<pacs008/>");   // credit body intact
        creditRow.TransferAmount.Should().Be(123.45m);
        var responseRow = rows.Single(r => r.MsgType == "pacs.002");
        responseRow.XmlMsgSystemA.Should().Be("<pacs002/>");
        responseRow.TxStatus.Should().Be("ACSP");
    }

    private static string Uid() => Guid.NewGuid().ToString("N")[..16];
}
