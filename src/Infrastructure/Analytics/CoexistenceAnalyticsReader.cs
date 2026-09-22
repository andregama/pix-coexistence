using ConvivenciaPix.Application.Common;
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
    private const string Pacs008Type = "pacs.008";
    private const string Pacs002Type = "pacs.002";
    private const string Pacs004Type = "pacs.004";

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

        var perSystem = await BuildPerSystemAsync(received, sent, cancellationToken);

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
            RecentErrors: recentErrors,
            PerSystem: perSystem);
    }

    /// <summary>
    /// Per-system scoreboards: transfer (pacs.008) / refund (pacs.004) counts and summed amounts
    /// (transfer + withdrawal), split sent vs received, attributed to a system by which facet the row
    /// carries (XmlMsgSystemA / XmlMsgSystemB present). One grouped projection per table keeps this to
    /// two round-trips. Totals across all statuses.
    /// </summary>
    private static async Task<PerSystemBreakdownDto> BuildPerSystemAsync(
        IQueryable<SpiReceivedMsg> received, IQueryable<SpiSentMsg> sent, CancellationToken ct)
    {
        var recv = await received
            .GroupBy(_ => 1)
            .Select(g => new
            {
                // System A facet (XmlMsgSystemA present).
                ATransferCount = g.Sum(x => x.XmlMsgSystemA != null && x.MsgType == Pacs008Type ? 1L : 0L),
                ATransferAmount = g.Sum(x => x.XmlMsgSystemA != null && x.MsgType == Pacs008Type
                    ? (x.TransferAmount ?? 0m) + (x.WithdrawalAmount ?? 0m) : 0m),
                ARefundCount = g.Sum(x => x.XmlMsgSystemA != null && x.MsgType == Pacs004Type ? 1L : 0L),
                ARefundAmount = g.Sum(x => x.XmlMsgSystemA != null && x.MsgType == Pacs004Type
                    ? (x.TransferAmount ?? 0m) + (x.WithdrawalAmount ?? 0m) : 0m),
                // System B facet (XmlMsgSystemB present).
                BTransferCount = g.Sum(x => x.XmlMsgSystemB != null && x.MsgType == Pacs008Type ? 1L : 0L),
                BTransferAmount = g.Sum(x => x.XmlMsgSystemB != null && x.MsgType == Pacs008Type
                    ? (x.TransferAmount ?? 0m) + (x.WithdrawalAmount ?? 0m) : 0m),
                BRefundCount = g.Sum(x => x.XmlMsgSystemB != null && x.MsgType == Pacs004Type ? 1L : 0L),
                BRefundAmount = g.Sum(x => x.XmlMsgSystemB != null && x.MsgType == Pacs004Type
                    ? (x.TransferAmount ?? 0m) + (x.WithdrawalAmount ?? 0m) : 0m),
            })
            .FirstOrDefaultAsync(ct);

        var snt = await sent
            .GroupBy(_ => 1)
            .Select(g => new
            {
                ATransferCount = g.Sum(x => x.XmlMsgSystemA != null && x.MsgType == Pacs008Type ? 1L : 0L),
                ATransferAmount = g.Sum(x => x.XmlMsgSystemA != null && x.MsgType == Pacs008Type
                    ? (x.TransferAmount ?? 0m) + (x.WithdrawalAmount ?? 0m) : 0m),
                ARefundCount = g.Sum(x => x.XmlMsgSystemA != null && x.MsgType == Pacs004Type ? 1L : 0L),
                ARefundAmount = g.Sum(x => x.XmlMsgSystemA != null && x.MsgType == Pacs004Type
                    ? (x.TransferAmount ?? 0m) + (x.WithdrawalAmount ?? 0m) : 0m),
                BTransferCount = g.Sum(x => x.XmlMsgSystemB != null && x.MsgType == Pacs008Type ? 1L : 0L),
                BTransferAmount = g.Sum(x => x.XmlMsgSystemB != null && x.MsgType == Pacs008Type
                    ? (x.TransferAmount ?? 0m) + (x.WithdrawalAmount ?? 0m) : 0m),
                BRefundCount = g.Sum(x => x.XmlMsgSystemB != null && x.MsgType == Pacs004Type ? 1L : 0L),
                BRefundAmount = g.Sum(x => x.XmlMsgSystemB != null && x.MsgType == Pacs004Type
                    ? (x.TransferAmount ?? 0m) + (x.WithdrawalAmount ?? 0m) : 0m),
            })
            .FirstOrDefaultAsync(ct);

        var systemA = new SystemFlowStatsDto(
            TransfersSentCount: snt?.ATransferCount ?? 0, TransfersSentAmount: snt?.ATransferAmount ?? 0m,
            TransfersReceivedCount: recv?.ATransferCount ?? 0, TransfersReceivedAmount: recv?.ATransferAmount ?? 0m,
            RefundsSentCount: snt?.ARefundCount ?? 0, RefundsSentAmount: snt?.ARefundAmount ?? 0m,
            RefundsReceivedCount: recv?.ARefundCount ?? 0, RefundsReceivedAmount: recv?.ARefundAmount ?? 0m);

        var systemB = new SystemFlowStatsDto(
            TransfersSentCount: snt?.BTransferCount ?? 0, TransfersSentAmount: snt?.BTransferAmount ?? 0m,
            TransfersReceivedCount: recv?.BTransferCount ?? 0, TransfersReceivedAmount: recv?.BTransferAmount ?? 0m,
            RefundsSentCount: snt?.BRefundCount ?? 0, RefundsSentAmount: snt?.BRefundAmount ?? 0m,
            RefundsReceivedCount: recv?.BRefundCount ?? 0, RefundsReceivedAmount: recv?.BRefundAmount ?? 0m);

        return new PerSystemBreakdownDto(systemA, systemB);
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

    public async Task<ErrorTimeSeriesDto> GetErrorTimeSeriesAsync(
        DateTime? from, DateTime? to, CancellationToken cancellationToken = default)
    {
        // Daily buckets of propagated error codes: per day, rows carrying a System A vs System B error.
        // Inbound (received) and outbound (sent) are counted separately then merged by day index.
        // DateDiffDay(epoch, CreatedAt) is the day index (translated server-side, like the trend query).
        var epoch = DateTime.UnixEpoch;

        var recvRaw = await FilterReceived(_db.SpiReceivedMsgs.AsNoTracking(), from, to)
            .GroupBy(x => EF.Functions.DateDiffDay(epoch, x.CreatedAt))
            .Select(g => new
            {
                DayIndex = g.Key,
                A = g.Sum(x => x.SystemAErrorCode != null ? 1L : 0L),
                B = g.Sum(x => x.SystemBErrorCode != null ? 1L : 0L),
            })
            .ToListAsync(cancellationToken);

        var sentRaw = await FilterSent(_db.SpiSentMsgs.AsNoTracking(), from, to)
            .GroupBy(x => EF.Functions.DateDiffDay(epoch, x.CreatedAt))
            .Select(g => new
            {
                DayIndex = g.Key,
                A = g.Sum(x => x.SystemAErrorCode != null ? 1L : 0L),
                B = g.Sum(x => x.SystemBErrorCode != null ? 1L : 0L),
            })
            .ToListAsync(cancellationToken);

        // Merge inbound + outbound by day index (union of keys), sum A and B, order ascending.
        var points = recvRaw.Concat(sentRaw)
            .GroupBy(r => r.DayIndex)
            .Select(g => new ErrorPointDto(
                Day: epoch.AddDays(g.Key),
                SystemAErrors: g.Sum(r => r.A),
                SystemBErrors: g.Sum(r => r.B)))
            .OrderBy(p => p.Day)
            .ToList();

        return new ErrorTimeSeriesDto(from, to, points);
    }

    public async Task<AmountTimeSeriesDto> GetAmountTimeSeriesAsync(
        DateTime? from, DateTime? to, CancellationToken cancellationToken = default)
    {
        // Daily buckets of summed value (TransferAmount + WithdrawalAmount, nulls as 0), split success
        // vs failed, received vs sent, merged by UTC day.
        var epoch = DateTime.UnixEpoch;
        // A List (not an array) so .Contains binds to Enumerable/List.Contains — EF translates it to a
        // SQL IN clause; an array's .Contains can bind to the ReadOnlySpan extension, which EF cannot translate.
        var accepted = TxStatuses.Accepted.ToList();
        var rejectedMarker = SpiErrorCodes.RejectedTransfer;
        var sentRows = _db.SpiSentMsgs.AsNoTracking();
        var receivedRows = _db.SpiReceivedMsgs.AsNoTracking();

        // Received: a pacs.008 credit is successful only when System A's outbound pacs.002 was accepted
        // (TxStatus in the accepted set) AND an inbound pacs.002 exists that is not rejected (the RJCT
        // marker on SystemAErrorCode). Other amount-bearing rows (e.g. pacs.004 returns) keep the
        // error-code rule. The two EXISTS flags are computed in SQL; rows are grouped by day in memory
        // (the window is TTL-bounded, so per-row materialisation is cheap — mirrors BuildRecentErrors).
        var recvMaterialized = await FilterReceived(receivedRows, from, to)
            .Where(x => x.TransferAmount != null || x.WithdrawalAmount != null)
            .Select(x => new
            {
                x.CreatedAt,
                Amount = (x.TransferAmount ?? 0m) + (x.WithdrawalAmount ?? 0m),
                Success = x.MsgType == Pacs008Type
                    ? sentRows.Any(s => s.IdempotentId == x.IdempotentId && s.MsgType == Pacs002Type
                          && accepted.Contains(s.TxStatus!))
                      && receivedRows.Any(p => p.IdempotentId == x.IdempotentId && p.MsgType == Pacs002Type
                          && p.SystemAErrorCode != rejectedMarker)
                    : x.SystemAErrorCode == null && x.SystemBErrorCode == null,
            })
            .ToListAsync(cancellationToken);

        var recvByDay = recvMaterialized
            .GroupBy(r => DateTime.SpecifyKind(r.CreatedAt.Date, DateTimeKind.Utc))
            .ToDictionary(g => g.Key, g => new
            {
                Success = g.Where(r => r.Success).Sum(r => r.Amount),
                Failed = g.Where(r => !r.Success).Sum(r => r.Amount),
            });

        // Sent: unchanged — success = no error code on either side.
        var sentRaw = await FilterSent(sentRows, from, to)
            .GroupBy(x => EF.Functions.DateDiffDay(epoch, x.CreatedAt))
            .Select(g => new
            {
                DayIndex = g.Key,
                Success = g.Sum(x => x.SystemAErrorCode == null && x.SystemBErrorCode == null
                    ? (x.TransferAmount ?? 0m) + (x.WithdrawalAmount ?? 0m) : 0m),
                Failed = g.Sum(x => x.SystemAErrorCode != null || x.SystemBErrorCode != null
                    ? (x.TransferAmount ?? 0m) + (x.WithdrawalAmount ?? 0m) : 0m),
            })
            .ToListAsync(cancellationToken);

        var sentByDay = sentRaw.ToDictionary(r => epoch.AddDays(r.DayIndex), r => new { r.Success, r.Failed });

        var points = recvByDay.Keys.Union(sentByDay.Keys)
            .OrderBy(day => day)
            .Select(day =>
            {
                recvByDay.TryGetValue(day, out var r);
                sentByDay.TryGetValue(day, out var s);
                return new AmountPointDto(
                    Day: day,
                    ReceivedSuccess: r?.Success ?? 0m,
                    ReceivedFailed: r?.Failed ?? 0m,
                    SentSuccess: s?.Success ?? 0m,
                    SentFailed: s?.Failed ?? 0m);
            })
            .ToList();

        return new AmountTimeSeriesDto(from, to, points);
    }

    public async Task<CountTimeSeriesDto> GetCountTimeSeriesAsync(
        DateTime? from, DateTime? to, CancellationToken cancellationToken = default)
    {
        // Daily buckets of transfer/refund transaction COUNTS (pacs.008 + pacs.004), split success vs
        // failed, received vs sent, merged by UTC day. Mirrors GetAmountTimeSeriesAsync but counts rows
        // instead of summing amounts, and is scoped to transfers/refunds only.
        var epoch = DateTime.UnixEpoch;
        var accepted = TxStatuses.Accepted.ToList();
        var rejectedMarker = SpiErrorCodes.RejectedTransfer;
        var sentRows = _db.SpiSentMsgs.AsNoTracking();
        var receivedRows = _db.SpiReceivedMsgs.AsNoTracking();

        // Received: a pacs.008 credit is successful only when System A's outbound pacs.002 was accepted
        // AND an inbound pacs.002 exists that is not rejected (same ack-based rule as the amount series).
        // pacs.004 returns keep the plain error-code rule. Materialise then group by day in memory.
        var recvMaterialized = await FilterReceived(receivedRows, from, to)
            .Where(x => x.MsgType == Pacs008Type || x.MsgType == Pacs004Type)
            .Select(x => new
            {
                x.CreatedAt,
                Success = x.MsgType == Pacs008Type
                    ? sentRows.Any(s => s.IdempotentId == x.IdempotentId && s.MsgType == Pacs002Type
                          && accepted.Contains(s.TxStatus!))
                      && receivedRows.Any(p => p.IdempotentId == x.IdempotentId && p.MsgType == Pacs002Type
                          && p.SystemAErrorCode != rejectedMarker)
                    : x.SystemAErrorCode == null && x.SystemBErrorCode == null,
            })
            .ToListAsync(cancellationToken);

        var recvByDay = recvMaterialized
            .GroupBy(r => DateTime.SpecifyKind(r.CreatedAt.Date, DateTimeKind.Utc))
            .ToDictionary(g => g.Key, g => new
            {
                Success = g.LongCount(r => r.Success),
                Failed = g.LongCount(r => !r.Success),
            });

        // Sent: success = no error code on either side.
        var sentRaw = await FilterSent(sentRows, from, to)
            .Where(x => x.MsgType == Pacs008Type || x.MsgType == Pacs004Type)
            .GroupBy(x => EF.Functions.DateDiffDay(epoch, x.CreatedAt))
            .Select(g => new
            {
                DayIndex = g.Key,
                Success = g.Sum(x => x.SystemAErrorCode == null && x.SystemBErrorCode == null ? 1L : 0L),
                Failed = g.Sum(x => x.SystemAErrorCode != null || x.SystemBErrorCode != null ? 1L : 0L),
            })
            .ToListAsync(cancellationToken);

        var sentByDay = sentRaw.ToDictionary(r => epoch.AddDays(r.DayIndex), r => new { r.Success, r.Failed });

        var points = recvByDay.Keys.Union(sentByDay.Keys)
            .OrderBy(day => day)
            .Select(day =>
            {
                recvByDay.TryGetValue(day, out var r);
                sentByDay.TryGetValue(day, out var s);
                return new CountPointDto(
                    Day: day,
                    ReceivedSuccess: r?.Success ?? 0L,
                    ReceivedFailed: r?.Failed ?? 0L,
                    SentSuccess: s?.Success ?? 0L,
                    SentFailed: s?.Failed ?? 0L);
            })
            .ToList();

        return new CountTimeSeriesDto(from, to, points);
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
