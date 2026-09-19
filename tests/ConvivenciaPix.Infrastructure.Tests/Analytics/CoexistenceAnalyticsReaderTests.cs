using ConvivenciaPix.Application.Common;
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

    [Fact]
    public async Task PropagationTimeSeries_ReturnsDailyPropagatedPct_Ordered()
    {
        var type = "ts-" + Guid.NewGuid().ToString("N")[..8];
        var day1 = new DateTime(2026, 3, 17, 10, 0, 0, DateTimeKind.Utc);
        var day2 = new DateTime(2026, 3, 18, 10, 0, 0, DateTimeKind.Utc);

        await using (var ctx = _fixture.CreateDbContext())
        {
            // Day 1: 2 received, 1 propagated -> 50%. Day 2: 2 received, 2 propagated -> 100%.
            ctx.SpiReceivedMsgs.Add(FromA("d1a", type, xmlB: true, consumed: false, source: "MessageKey"));
            ctx.SpiReceivedMsgs.Add(FromA("d1b", type, xmlB: false, consumed: false, source: "MessageKey"));
            ctx.SpiReceivedMsgs.Add(FromA("d2a", type, xmlB: true, consumed: false, source: "MessageKey"));
            ctx.SpiReceivedMsgs.Add(FromA("d2b", type, xmlB: true, consumed: false, source: "MessageKey"));
            // A pibr.002 on day 1 that must be excluded from the totals.
            var echo = SpiReceivedMsg.CreateFromSystemA("echo" + type, "pibr.002", null, "<a/>", null);
            ctx.SpiReceivedMsgs.Add(echo);
            await ctx.SaveChangesAsync();

            foreach (var (key, day) in new[] { ("d1a", day1), ("d1b", day1), ("d2a", day2), ("d2b", day2), ("echo", day1) })
                await ctx.Database.ExecuteSqlRawAsync(
                    "UPDATE SpiReceivedMsg SET CreatedAt = {0} WHERE IdempotentId = {1}", day, key + type);
        }

        var series = await Reader(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .GetPropagationTimeSeriesAsync(
                from: new DateTime(2026, 3, 16, 0, 0, 0, DateTimeKind.Utc),
                to: new DateTime(2026, 3, 19, 0, 0, 0, DateTimeKind.Utc));

        series.Points.Should().HaveCount(2);
        series.Points[0].Day.Should().Be(new DateTime(2026, 3, 17));
        series.Points[0].Received.Should().Be(2); // pibr.002 excluded
        series.Points[0].Propagated.Should().Be(1);
        series.Points[0].PropagatedPct.Should().Be(50);
        series.Points[1].Day.Should().Be(new DateTime(2026, 3, 18));
        series.Points[1].Received.Should().Be(2);
        series.Points[1].Propagated.Should().Be(2);
        series.Points[1].PropagatedPct.Should().Be(100);
    }

    [Fact]
    public async Task ErrorTimeSeries_ReturnsDailySystemAAndBErrorCounts_Ordered()
    {
        var type = "et-" + Guid.NewGuid().ToString("N")[..8];
        // Unique historical days so aggregation over the whole window sees only this test's rows.
        var day1 = new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc);
        var day2 = new DateTime(2026, 5, 11, 10, 0, 0, DateTimeKind.Utc);

        await using (var ctx = _fixture.CreateDbContext())
        {
            // Day 1 — inbound A error, inbound B error, outbound A error, and a clean (no-error) row.
            ctx.SpiReceivedMsgs.Add(SpiReceivedMsg.CreateFromSystemA("rA" + type, type, null, "<a/>", errorCode: "EA1"));
            var recvB = SpiReceivedMsg.CreateFromSystemA("rB" + type, type, null, "<a/>", errorCode: null);
            recvB.SetSystemBXml("<b/>", "EB1");
            ctx.SpiReceivedMsgs.Add(recvB);
            ctx.SpiReceivedMsgs.Add(FromA("clean", type, xmlB: true, consumed: false, source: "MessageKey"));
            var sentA = SpiSentMsg.Create("sA" + type, type);
            sentA.UpdateFromSystemA("MSGA", "<a/>", "EA2");
            ctx.SpiSentMsgs.Add(sentA);
            // Day 1 — a pibr.002 carrying an error that must be excluded from the totals.
            ctx.SpiReceivedMsgs.Add(SpiReceivedMsg.CreateFromSystemA("echo" + type, "pibr.002", null, "<a/>", errorCode: "EX9"));

            // Day 2 — a single outbound B error.
            var sentB = SpiSentMsg.Create("sB" + type, type);
            sentB.UpdateFromSystemB("MSGB", "<b/>", "EB2");
            ctx.SpiSentMsgs.Add(sentB);

            await ctx.SaveChangesAsync();

            foreach (var (key, day) in new[] { ("rA", day1), ("rB", day1), ("clean", day1), ("echo", day1) })
                await ctx.Database.ExecuteSqlRawAsync(
                    "UPDATE SpiReceivedMsg SET CreatedAt = {0} WHERE IdempotentId = {1}", day, key + type);
            foreach (var (key, day) in new[] { ("sA", day1), ("sB", day2) })
                await ctx.Database.ExecuteSqlRawAsync(
                    "UPDATE SpiSentMsg SET CreatedAt = {0} WHERE IdempotentId = {1}", day, key + type);
        }

        var series = await Reader(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .GetErrorTimeSeriesAsync(
                from: new DateTime(2026, 5, 9, 0, 0, 0, DateTimeKind.Utc),
                to: new DateTime(2026, 5, 12, 0, 0, 0, DateTimeKind.Utc));

        series.Points.Should().HaveCount(2);
        series.Points[0].Day.Should().Be(new DateTime(2026, 5, 10));
        series.Points[0].SystemAErrors.Should().Be(2); // inbound EA1 + outbound EA2 (pibr.002 EX9 excluded)
        series.Points[0].SystemBErrors.Should().Be(1); // inbound EB1
        series.Points[1].Day.Should().Be(new DateTime(2026, 5, 11));
        series.Points[1].SystemAErrors.Should().Be(0);
        series.Points[1].SystemBErrors.Should().Be(1); // outbound EB2
    }

    [Fact]
    public async Task AmountTimeSeries_SumsReceivedAndSentBySuccessFailed_Ordered()
    {
        var type = "am-" + Guid.NewGuid().ToString("N")[..8];
        var day1 = new DateTime(2026, 6, 10, 10, 0, 0, DateTimeKind.Utc);
        var day2 = new DateTime(2026, 6, 11, 10, 0, 0, DateTimeKind.Utc);

        await using (var ctx = _fixture.CreateDbContext())
        {
            // Received — day1: success 100, failed 50; day2: success 200+25 withdrawal.
            var r1 = SpiReceivedMsg.CreateFromSystemA("r1" + type, type, null, "<a/>", errorCode: null);
            r1.SetAmounts(100m, 0m);
            var r2 = SpiReceivedMsg.CreateFromSystemA("r2" + type, type, null, "<a/>", errorCode: null);
            r2.SetSystemBXml("<b/>", "EB1"); // failed
            r2.SetAmounts(50m, 0m);
            var r3 = SpiReceivedMsg.CreateFromSystemA("r3" + type, type, null, "<a/>", errorCode: null);
            r3.SetAmounts(200m, 25m);
            // pibr.002 with an amount that must be excluded.
            var echo = SpiReceivedMsg.CreateFromSystemA("echo" + type, "pibr.002", null, "<a/>", errorCode: null);
            echo.SetAmounts(999m, 0m);
            ctx.SpiReceivedMsgs.AddRange(r1, r2, r3, echo);

            // Sent — day1: success 300; day2: failed 40+10 withdrawal.
            var s1 = SpiSentMsg.Create("s1" + type, type);
            s1.UpdateFromSystemA("MSGA1", "<a/>", null);
            s1.SetAmounts(300m, 0m);
            var s2 = SpiSentMsg.Create("s2" + type, type);
            s2.UpdateFromSystemA("MSGA2", "<a/>", "EA1"); // failed
            s2.SetAmounts(40m, 10m);
            ctx.SpiSentMsgs.AddRange(s1, s2);

            await ctx.SaveChangesAsync();

            foreach (var (key, day) in new[] { ("r1", day1), ("r2", day1), ("r3", day2), ("echo", day1) })
                await ctx.Database.ExecuteSqlRawAsync(
                    "UPDATE SpiReceivedMsg SET CreatedAt = {0} WHERE IdempotentId = {1}", day, key + type);
            foreach (var (key, day) in new[] { ("s1", day1), ("s2", day2) })
                await ctx.Database.ExecuteSqlRawAsync(
                    "UPDATE SpiSentMsg SET CreatedAt = {0} WHERE IdempotentId = {1}", day, key + type);
        }

        var series = await Reader(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .GetAmountTimeSeriesAsync(
                from: new DateTime(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc),
                to: new DateTime(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc));

        series.Points.Should().HaveCount(2);
        series.Points[0].Day.Should().Be(new DateTime(2026, 6, 10));
        series.Points[0].ReceivedSuccess.Should().Be(100m); // pibr.002 999 excluded
        series.Points[0].ReceivedFailed.Should().Be(50m);
        series.Points[0].SentSuccess.Should().Be(300m);
        series.Points[0].SentFailed.Should().Be(0m);
        series.Points[1].Day.Should().Be(new DateTime(2026, 6, 11));
        series.Points[1].ReceivedSuccess.Should().Be(225m); // 200 transfer + 25 withdrawal
        series.Points[1].ReceivedFailed.Should().Be(0m);
        series.Points[1].SentSuccess.Should().Be(0m);
        series.Points[1].SentFailed.Should().Be(50m); // 40 + 10
    }

    [Fact]
    public async Task AmountTimeSeries_ReceivedCredit_SuccessOnlyWhenAckAcceptedAndInboundNotRejected()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var day = new DateTime(2026, 7, 10, 10, 0, 0, DateTimeKind.Utc);

        // Credit A: outbound ack ACCC + inbound pacs.002 no error -> success (100).
        // Credit B: outbound ack ACCC + inbound pacs.002 rejected  -> failed (50).
        // Credit C: inbound pacs.002 ok but NO accepted outbound ack -> failed (25).
        await using (var ctx = _fixture.CreateDbContext())
        {
            foreach (var (key, amount) in new[] { ("A", 100m), ("B", 50m), ("C", 25m) })
            {
                var credit = SpiReceivedMsg.CreateFromSystemA($"E2E-{key}-{tag}", "pacs.008", null, "<a/>", errorCode: null);
                credit.SetAmounts(amount, 0m);
                ctx.SpiReceivedMsgs.Add(credit);
            }
            // Inbound pacs.002 rows (Bacen replies): A/C no error, B rejected.
            var inA = SpiReceivedMsg.CreateFromSystemA($"E2E-A-{tag}", "pacs.002", null, "<p/>", errorCode: null);
            inA.SetTxStatus("ACSP");
            var inB = SpiReceivedMsg.CreateFromSystemA($"E2E-B-{tag}", "pacs.002", null, "<p/>", errorCode: SpiErrorCodes.RejectedTransfer);
            inB.SetTxStatus("RJCT");
            var inC = SpiReceivedMsg.CreateFromSystemA($"E2E-C-{tag}", "pacs.002", null, "<p/>", errorCode: null);
            inC.SetTxStatus("ACSP");
            ctx.SpiReceivedMsgs.AddRange(inA, inB, inC);
            // Outbound pacs.002 acks in SpiSentMsg: A/B accepted (ACCC), C absent.
            foreach (var key in new[] { "A", "B" })
            {
                var ack = SpiSentMsg.Create($"E2E-{key}-{tag}", "pacs.002");
                ack.UpdateFromSystemA($"MSGA-{key}", "<a/>", null);
                ack.SetTxStatus("ACCC");
                ctx.SpiSentMsgs.Add(ack);
            }
            await ctx.SaveChangesAsync();

            foreach (var key in new[] { "A", "B", "C" })
                await ctx.Database.ExecuteSqlRawAsync(
                    "UPDATE SpiReceivedMsg SET CreatedAt = {0} WHERE IdempotentId = {1} AND MsgType = 'pacs.008'",
                    day, $"E2E-{key}-{tag}");
        }

        var series = await Reader(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .GetAmountTimeSeriesAsync(
                from: new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc),
                to: new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc));

        var point = series.Points.Single(p => p.Day == new DateTime(2026, 7, 10));
        point.ReceivedSuccess.Should().Be(100m);   // only credit A
        point.ReceivedFailed.Should().Be(75m);     // B (inbound rejected) + C (no accepted ack)
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
