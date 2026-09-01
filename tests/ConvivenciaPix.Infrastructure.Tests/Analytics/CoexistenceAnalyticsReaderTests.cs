using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Infrastructure.Analytics;
using ConvivenciaPix.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Analytics;

public sealed class CoexistenceAnalyticsReaderTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public CoexistenceAnalyticsReaderTests(SqlServerFixture fixture) => _fixture = fixture;

    private CoexistenceAnalyticsReader Reader(DateTime cutoff) =>
        new(_fixture.CreateDbContext(), Options.Create(new AnalyticsOptions { ConsumptionTrackingSince = cutoff }));

    // Each test tags its rows with a unique msg-type token and filters the summary to that
    // token's rows via ByMsgType, so tests stay independent on the shared container.
    [Fact]
    public async Task Summary_ComputesFunnelErrorsAndBreakdowns()
    {
        var type = "t-" + Guid.NewGuid().ToString("N")[..8];

        await using (var ctx = _fixture.CreateDbContext())
        {
            // Received on A only (propagation gap).
            ctx.SpiReceivedMsgs.Add(FromA("gap", type, xmlB: false, consumed: false, source: "MessageKey"));
            // Propagated to B, not yet consumed (awaiting).
            ctx.SpiReceivedMsgs.Add(FromA("await", type, xmlB: true, consumed: false, source: "MessageKey"));
            // Propagated and consumed.
            ctx.SpiReceivedMsgs.Add(FromA("done", type, xmlB: true, consumed: true, source: "DerivedKey"));
            // A B-side error.
            var err = FromA("err", type, xmlB: true, consumed: true, source: "MessageKey");
            err.SetSystemBXml("<b/>", "AB09");
            ctx.SpiReceivedMsgs.Add(err);

            ctx.SpiDiscrepancies.Add(SpiDiscrepancy.Create("done" + type, type, "Amount", "10.00", "10.01"));
            await ctx.SaveChangesAsync();
        }

        // Cutoff in the past → the seeded (now) rows use real ConsumedAt tracking.
        var summary = await Reader(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .GetSummaryAsync(from: null, to: null);

        var mine = summary.ByMsgType.Single(x => x.MsgType == type);
        mine.ReceivedFromA.Should().Be(4);
        mine.PropagatedToB.Should().Be(3);
        mine.ConsumedByB.Should().Be(2); // done + err (await is not acked and is post-cutoff)

        summary.CorrelationSource.Should().Contain(x => x.Label == "DerivedKey");
        summary.CorrelationSource.Should().Contain(x => x.Label == "MessageKey");

        summary.Errors.Discrepancies.Should().BeGreaterThanOrEqualTo(1);
        summary.RecentErrors.Should().Contain(e => e.ErrorCode == "AB09" && e.System == "B");
    }

    [Fact]
    public async Task Summary_PreCutoffPropagatedRow_CountsAsConsumed()
    {
        var type = "t-" + Guid.NewGuid().ToString("N")[..8];
        var cutoff = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var ctx = _fixture.CreateDbContext())
        {
            // Propagated to B, never acked, created BEFORE cutoff → backfilled as consumed.
            ctx.SpiReceivedMsgs.Add(FromA("pre", type, xmlB: true, consumed: false, source: "MessageKey"));
            // Propagated to B, never acked, created AFTER cutoff → still awaiting (not consumed).
            ctx.SpiReceivedMsgs.Add(FromA("post", type, xmlB: true, consumed: false, source: "MessageKey"));
            // Explicitly acked → consumed regardless of date.
            ctx.SpiReceivedMsgs.Add(FromA("acked", type, xmlB: true, consumed: true, source: "MessageKey"));
            await ctx.SaveChangesAsync();

            await ctx.Database.ExecuteSqlRawAsync(
                "UPDATE SpiReceivedMsg SET CreatedAt = {0} WHERE IdempotentId = {1}",
                cutoff.AddDays(-1), "pre" + type);
            await ctx.Database.ExecuteSqlRawAsync(
                "UPDATE SpiReceivedMsg SET CreatedAt = {0} WHERE IdempotentId = {1}",
                cutoff.AddDays(1), "post" + type);
        }

        var summary = await Reader(cutoff).GetSummaryAsync(from: null, to: null);

        var mine = summary.ByMsgType.Single(x => x.MsgType == type);
        mine.PropagatedToB.Should().Be(3);
        mine.ConsumedByB.Should().Be(2); // pre (backfilled) + acked; post is still awaiting
    }

    [Fact]
    public async Task Summary_ExcludesPibr002FromCounts()
    {
        var type = "t-" + Guid.NewGuid().ToString("N")[..8];

        await using (var ctx = _fixture.CreateDbContext())
        {
            ctx.SpiReceivedMsgs.Add(FromA("ok", type, xmlB: true, consumed: true, source: "MessageKey"));

            // A pibr.002 row (proxy Echo reply) with an error — must be excluded everywhere.
            var echo = SpiReceivedMsg.CreateFromSystemA("echo" + type, "pibr.002", null, "<a/>", "AB99");
            echo.SetSystemBXml("<b/>", "AB99");
            ctx.SpiReceivedMsgs.Add(echo);
            await ctx.SaveChangesAsync();
        }

        var summary = await Reader(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .GetSummaryAsync(from: null, to: null);

        summary.ByMsgType.Should().NotContain(x => x.MsgType == "pibr.002");
        summary.RecentErrors.Should().NotContain(e => e.ErrorCode == "AB99");
        summary.ByMsgType.Should().Contain(x => x.MsgType == type);
    }

    [Fact]
    public async Task Summary_OutboundByMsgType_ReportsSentAndCorrelatedPerType()
    {
        var type = "o-" + Guid.NewGuid().ToString("N")[..8];

        await using (var ctx = _fixture.CreateDbContext())
        {
            // Two correlated (both sides) + one single-sided (A only) for the same type.
            ctx.SpiSentMsgs.Add(SentPair(type, "a1", correlated: true));
            ctx.SpiSentMsgs.Add(SentPair(type, "a2", correlated: true));
            ctx.SpiSentMsgs.Add(SentPair(type, "a3", correlated: false));
            await ctx.SaveChangesAsync();
        }

        var summary = await Reader(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .GetSummaryAsync(from: null, to: null);

        var mine = summary.OutboundByMsgType.Single(x => x.MsgType == type);
        mine.Total.Should().Be(3);
        mine.Correlated.Should().Be(2);
    }

    [Fact]
    public async Task Summary_InboundLatency_MeasuresConsumedRows()
    {
        var type = "l-" + Guid.NewGuid().ToString("N")[..8];

        await using (var ctx = _fixture.CreateDbContext())
        {
            ctx.SpiReceivedMsgs.Add(FromA("lat1", type, xmlB: true, consumed: true, source: "MessageKey"));
            ctx.SpiReceivedMsgs.Add(FromA("lat2", type, xmlB: true, consumed: true, source: "MessageKey"));
            await ctx.SaveChangesAsync();

            // Force known end-to-end gaps: 1000 ms and 3000 ms.
            var baseTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            await ctx.Database.ExecuteSqlRawAsync(
                "UPDATE SpiReceivedMsg SET CreatedAt = {0}, ConsumedAt = {1} WHERE IdempotentId = {2}",
                baseTime, baseTime.AddMilliseconds(1000), "lat1" + type);
            await ctx.Database.ExecuteSqlRawAsync(
                "UPDATE SpiReceivedMsg SET CreatedAt = {0}, ConsumedAt = {1} WHERE IdempotentId = {2}",
                baseTime, baseTime.AddMilliseconds(3000), "lat2" + type);
        }

        // Scope the window so only this test's two rows contribute to the aggregate.
        var summary = await Reader(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .GetSummaryAsync(
                from: new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc),
                to: new DateTime(2026, 1, 1, 13, 0, 0, DateTimeKind.Utc));

        summary.Latency.InboundEndToEnd.Count.Should().Be(2);
        summary.Latency.InboundEndToEnd.AvgMs.Should().Be(2000);
        summary.Latency.InboundEndToEnd.MaxMs.Should().Be(3000);
    }

    private static SpiSentMsg SentPair(string type, string key, bool correlated)
    {
        var msg = SpiSentMsg.Create(key + type, type);
        msg.UpdateFromSystemA("MSGA-" + key, "<a/>", null);
        if (correlated)
            msg.UpdateFromSystemB("MSGB-" + key, "<b/>", null);
        return msg;
    }

    private static SpiReceivedMsg FromA(string key, string type, bool xmlB, bool consumed, string source)
    {
        var msg = SpiReceivedMsg.CreateFromSystemA(key + type, type, msgId: null, "<a/>", errorCode: null);
        msg.SetCorrelationSource(source);
        if (xmlB)
        {
            msg.SetSystemBXml("<b/>");
            msg.SetPiResourceId("rid-" + key + type);
        }
        if (consumed) msg.MarkConsumed();
        return msg;
    }
}
