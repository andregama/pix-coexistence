using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Infrastructure.Analytics;
using ConvivenciaPix.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Analytics;

public sealed class CoexistenceAnalyticsReaderTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public CoexistenceAnalyticsReaderTests(SqlServerFixture fixture) => _fixture = fixture;

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

        var reader = new CoexistenceAnalyticsReader(_fixture.CreateDbContext());
        var summary = await reader.GetSummaryAsync(from: null, to: null);

        var mine = summary.ByMsgType.Single(x => x.MsgType == type);
        mine.ReceivedFromA.Should().Be(4);
        mine.PropagatedToB.Should().Be(3);
        mine.ConsumedByB.Should().Be(2);

        // Correlation source split includes this test's rows.
        summary.CorrelationSource.Should().Contain(x => x.Label == "DerivedKey");
        summary.CorrelationSource.Should().Contain(x => x.Label == "MessageKey");

        // Discrepancy + recent error surfaced.
        summary.Errors.Discrepancies.Should().BeGreaterThanOrEqualTo(1);
        summary.RecentErrors.Should().Contain(e => e.ErrorCode == "AB09" && e.System == "B");
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
