using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Linq.Expressions;

namespace ConvivenciaPix.Infrastructure.Analytics;

/// <summary>
/// EF Core implementation of <see cref="ICoexistenceAnalyticsReader"/>. All queries are read-only
/// aggregates against the coexistence tables — no tracking, no writes.
/// </summary>
public sealed class CoexistenceAnalyticsReader : ICoexistenceAnalyticsReader
{
    private const int RecentErrorLimit = 50;
    private const string UnknownLabel = "Unknown";

    /// <summary>
    /// Proxy-synthesised SPI Echo reply (pibr.002). It has no System A ↔ System B coexistence flow,
    /// so it is excluded from every analytics count.
    /// </summary>
    private const string ExcludedMsgType = "pibr.002";

    private readonly CoexistenceDbContext _db;
    private readonly DateTime _consumptionTrackingSince;

    public CoexistenceAnalyticsReader(CoexistenceDbContext db, IOptions<AnalyticsOptions> options)
    {
        _db = db;
        _consumptionTrackingSince = options.Value.ConsumptionTrackingSince;
    }

    public async Task<CoexistenceSummaryDto> GetSummaryAsync(
        DateTime? from, DateTime? to, CancellationToken cancellationToken = default)
    {
        var cutoff = _consumptionTrackingSince;

        // A row counts as consumed by System B if it was explicitly acked (ConsumedAt set), OR it
        // predates the consumption-tracking columns (created before the cutoff) and was at least
        // propagated to B — those rows can never have ConsumedAt, so they are backfilled as consumed.
        Expression<Func<SpiReceivedMsg, bool>> isConsumed =
            x => x.ConsumedAt != null || (x.CreatedAt < cutoff && x.XmlMsgSystemB != null);

        // Propagated to B but not yet consumed: only rows on/after the cutoff can be "awaiting",
        // since pre-cutoff propagated rows are treated as already consumed above.
        Expression<Func<SpiReceivedMsg, bool>> isAwaiting =
            x => x.XmlMsgSystemB != null && x.ConsumedAt == null && x.CreatedAt >= cutoff;

        var received = FilterReceived(_db.SpiReceivedMsgs.AsNoTracking(), from, to);
        var sent = FilterSent(_db.SpiSentMsgs.AsNoTracking(), from, to);

        var discrepancies = _db.SpiDiscrepancies.AsNoTracking().Where(x => x.MsgType != ExcludedMsgType);
        if (from is not null) discrepancies = discrepancies.Where(x => x.DetectedAt >= from);
        if (to is not null) discrepancies = discrepancies.Where(x => x.DetectedAt <= to);

        var inbound = new InboundFunnelDto(
            ReceivedFromA: await received.CountAsync(x => x.XmlMsgSystemA != null, cancellationToken),
            PropagatedToB: await received.CountAsync(x => x.XmlMsgSystemB != null, cancellationToken),
            ConsumedByB: await received.CountAsync(isConsumed, cancellationToken),
            AwaitingConsumption: await received.CountAsync(isAwaiting, cancellationToken),
            PropagationGap: await received.CountAsync(
                x => x.XmlMsgSystemA != null && x.XmlMsgSystemB == null, cancellationToken));

        var outbound = new OutboundStatsDto(
            Total: await sent.CountAsync(cancellationToken),
            CorrelatedPairs: await sent.CountAsync(
                x => x.MsgIdSystemA != null && x.MsgIdSystemB != null, cancellationToken));

        var errors = new ErrorStatsDto(
            InboundSystemAErrors: await received.CountAsync(x => x.SystemAErrorCode != null, cancellationToken),
            InboundSystemBErrors: await received.CountAsync(x => x.SystemBErrorCode != null, cancellationToken),
            OutboundSystemAErrors: await sent.CountAsync(x => x.SystemAErrorCode != null, cancellationToken),
            OutboundSystemBErrors: await sent.CountAsync(x => x.SystemBErrorCode != null, cancellationToken),
            Discrepancies: await discrepancies.CountAsync(cancellationToken));

        var correlationSourceRaw = await received
            .GroupBy(x => x.CorrelationSource)
            .Select(g => new { g.Key, Count = g.LongCount() })
            .ToListAsync(cancellationToken);
        var correlationSource = correlationSourceRaw
            .Select(x => new LabelCountDto(x.Key ?? UnknownLabel, x.Count))
            .OrderByDescending(x => x.Count)
            .ToList();

        // Project the grouped conditional sums into an anonymous type (EF-translatable), then map
        // to the DTO and order client-side — EF Core can't build a custom record in the projection.
        var byMsgTypeRaw = await received
            .GroupBy(x => x.MsgType)
            .Select(g => new
            {
                MsgType = g.Key,
                ReceivedFromA = g.Sum(x => x.XmlMsgSystemA != null ? 1L : 0L),
                PropagatedToB = g.Sum(x => x.XmlMsgSystemB != null ? 1L : 0L),
                ConsumedByB = g.Sum(x =>
                    (x.ConsumedAt != null || (x.CreatedAt < cutoff && x.XmlMsgSystemB != null)) ? 1L : 0L),
            })
            .ToListAsync(cancellationToken);
        var byMsgType = byMsgTypeRaw
            .Select(x => new MsgTypeBreakdownDto(x.MsgType, x.ReceivedFromA, x.PropagatedToB, x.ConsumedByB))
            .OrderByDescending(x => x.ReceivedFromA)
            .ToList();

        var discrepanciesByFieldRaw = await discrepancies
            .GroupBy(x => x.Field)
            .Select(g => new { Field = g.Key, Count = g.LongCount() })
            .ToListAsync(cancellationToken);
        var discrepanciesByField = discrepanciesByFieldRaw
            .Select(x => new LabelCountDto(x.Field, x.Count))
            .OrderByDescending(x => x.Count)
            .ToList();

        // Outbound (PSP→SPI) sent/correlated counts per message type — surfaces trck.002 and every
        // other outbound request. Anonymous-type projection then client-side map/order (EF-translatable).
        var outboundByTypeRaw = await sent
            .GroupBy(x => x.MsgType)
            .Select(g => new
            {
                MsgType = g.Key,
                Total = g.LongCount(),
                Correlated = g.Sum(x => x.MsgIdSystemA != null && x.MsgIdSystemB != null ? 1L : 0L),
            })
            .ToListAsync(cancellationToken);
        var outboundByMsgType = outboundByTypeRaw
            .Select(x => new OutboundMsgTypeBreakdownDto(x.MsgType, x.Total, x.Correlated))
            .OrderByDescending(x => x.Total)
            .ToList();

        var latency = new ReplicationLatencyDto(
            InboundEndToEnd: Summarize(await received
                .Where(x => x.ConsumedAt != null)
                .Select(x => EF.Functions.DateDiffMillisecond(x.CreatedAt, x.ConsumedAt!.Value))
                .ToListAsync(cancellationToken)),
            OutboundCorrelation: Summarize(await sent
                .Where(x => x.UpdatedAt != null && x.MsgIdSystemA != null && x.MsgIdSystemB != null)
                .Select(x => EF.Functions.DateDiffMillisecond(x.CreatedAt, x.UpdatedAt!.Value))
                .ToListAsync(cancellationToken)));

        var recentErrors = await BuildRecentErrorsAsync(received, sent, cancellationToken);

        return new CoexistenceSummaryDto(
            From: from,
            To: to,
            GeneratedAt: DateTime.UtcNow,
            Inbound: inbound,
            Outbound: outbound,
            Errors: errors,
            CorrelationSource: correlationSource,
            ByMsgType: byMsgType,
            OutboundByMsgType: outboundByMsgType,
            Latency: latency,
            DiscrepanciesByField: discrepanciesByField,
            RecentErrors: recentErrors);
    }

    /// <summary>Computes count/avg/p50/p95/max over a set of millisecond durations (negatives clamped to 0).</summary>
    private static LatencyStatsDto Summarize(IReadOnlyList<int> durationsMs)
    {
        if (durationsMs.Count == 0)
            return LatencyStatsDto.Empty;

        var sorted = durationsMs.Select(d => (double)Math.Max(0, d)).OrderBy(d => d).ToArray();
        return new LatencyStatsDto(
            Count: sorted.Length,
            AvgMs: sorted.Average(),
            P50Ms: Percentile(sorted, 0.50),
            P95Ms: Percentile(sorted, 0.95),
            MaxMs: sorted[^1]);
    }

    // Nearest-rank percentile over a pre-sorted ascending array (index = ceil(p*n) − 1).
    private static double Percentile(double[] sortedAsc, double p)
    {
        var rank = (int)Math.Ceiling(p * sortedAsc.Length);
        var index = Math.Clamp(rank - 1, 0, sortedAsc.Length - 1);
        return sortedAsc[index];
    }

    private static async Task<IReadOnlyList<RecentErrorDto>> BuildRecentErrorsAsync(
        IQueryable<SpiReceivedMsg> received, IQueryable<SpiSentMsg> sent, CancellationToken ct)
    {
        var inboundRows = await received
            .Where(x => x.SystemAErrorCode != null || x.SystemBErrorCode != null)
            .OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt)
            .Take(RecentErrorLimit)
            .Select(x => new { x.IdempotentId, x.MsgType, x.SystemAErrorCode, x.SystemBErrorCode, x.UpdatedAt, x.CreatedAt })
            .ToListAsync(ct);

        var outboundRows = await sent
            .Where(x => x.SystemAErrorCode != null || x.SystemBErrorCode != null)
            .OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt)
            .Take(RecentErrorLimit)
            .Select(x => new { x.IdempotentId, x.MsgType, x.SystemAErrorCode, x.SystemBErrorCode, x.UpdatedAt, x.CreatedAt })
            .ToListAsync(ct);

        var errors = new List<RecentErrorDto>();
        foreach (var r in inboundRows)
        {
            var at = r.UpdatedAt ?? r.CreatedAt;
            if (r.SystemAErrorCode != null)
                errors.Add(new RecentErrorDto(r.IdempotentId, r.MsgType, "Inbound", "A", r.SystemAErrorCode, at));
            if (r.SystemBErrorCode != null)
                errors.Add(new RecentErrorDto(r.IdempotentId, r.MsgType, "Inbound", "B", r.SystemBErrorCode, at));
        }
        foreach (var r in outboundRows)
        {
            var at = r.UpdatedAt ?? r.CreatedAt;
            if (r.SystemAErrorCode != null)
                errors.Add(new RecentErrorDto(r.IdempotentId, r.MsgType, "Outbound", "A", r.SystemAErrorCode, at));
            if (r.SystemBErrorCode != null)
                errors.Add(new RecentErrorDto(r.IdempotentId, r.MsgType, "Outbound", "B", r.SystemBErrorCode, at));
        }

        return errors
            .OrderByDescending(x => x.At)
            .Take(RecentErrorLimit)
            .ToList();
    }

    public async Task<PropagationTimeSeriesDto> GetPropagationTimeSeriesAsync(
        DateTime? from, DateTime? to, CancellationToken cancellationToken = default)
    {
        // Daily buckets: for messages received from System A each day, how many were propagated to B.
        // DateDiffDay(epoch, CreatedAt) is the day index (translated server-side, like the latency queries).
        var epoch = DateTime.UnixEpoch;
        var received = FilterReceived(_db.SpiReceivedMsgs.AsNoTracking(), from, to)
            .Where(x => x.XmlMsgSystemA != null);

        var raw = await received
            .GroupBy(x => EF.Functions.DateDiffDay(epoch, x.CreatedAt))
            .Select(g => new
            {
                DayIndex = g.Key,
                Received = g.LongCount(),
                Propagated = g.Sum(x => x.XmlMsgSystemB != null ? 1L : 0L),
            })
            .ToListAsync(cancellationToken);

        var points = raw
            .OrderBy(r => r.DayIndex)
            .Select(r => new PropagationPointDto(
                Day: epoch.AddDays(r.DayIndex),
                Received: r.Received,
                Propagated: r.Propagated,
                PropagatedPct: r.Received > 0 ? Math.Round(r.Propagated * 100.0 / r.Received, 1) : 0))
            .ToList();

        return new PropagationTimeSeriesDto(from, to, points);
    }

    // pibr.002 (proxy-synthesised Echo reply) is excluded from all counts; date bounds apply to CreatedAt.
    private static IQueryable<SpiReceivedMsg> FilterReceived(IQueryable<SpiReceivedMsg> q, DateTime? from, DateTime? to)
    {
        q = q.Where(x => x.MsgType != ExcludedMsgType);
        if (from is not null) q = q.Where(x => x.CreatedAt >= from);
        if (to is not null) q = q.Where(x => x.CreatedAt <= to);
        return q;
    }

    private static IQueryable<SpiSentMsg> FilterSent(IQueryable<SpiSentMsg> q, DateTime? from, DateTime? to)
    {
        q = q.Where(x => x.MsgType != ExcludedMsgType);
        if (from is not null) q = q.Where(x => x.CreatedAt >= from);
        if (to is not null) q = q.Where(x => x.CreatedAt <= to);
        return q;
    }
}
