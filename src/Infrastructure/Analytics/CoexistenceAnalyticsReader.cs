using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ConvivenciaPix.Infrastructure.Analytics;

/// <summary>
/// EF Core implementation of <see cref="ICoexistenceAnalyticsReader"/>. All queries are read-only
/// aggregates against the coexistence tables — no tracking, no writes.
/// </summary>
public sealed class CoexistenceAnalyticsReader : ICoexistenceAnalyticsReader
{
    private const int RecentErrorLimit = 50;
    private const string UnknownLabel = "Unknown";

    private readonly CoexistenceDbContext _db;

    public CoexistenceAnalyticsReader(CoexistenceDbContext db) => _db = db;

    public async Task<CoexistenceSummaryDto> GetSummaryAsync(
        DateTime? from, DateTime? to, CancellationToken cancellationToken = default)
    {
        var received = FilterByCreatedAt(_db.SpiReceivedMsgs.AsNoTracking(), from, to);
        var sent = FilterByCreatedAt(_db.SpiSentMsgs.AsNoTracking(), from, to);

        var discrepancies = _db.SpiDiscrepancies.AsNoTracking().AsQueryable();
        if (from is not null) discrepancies = discrepancies.Where(x => x.DetectedAt >= from);
        if (to is not null) discrepancies = discrepancies.Where(x => x.DetectedAt <= to);

        var inbound = new InboundFunnelDto(
            ReceivedFromA: await received.CountAsync(x => x.XmlMsgSystemA != null, cancellationToken),
            PropagatedToB: await received.CountAsync(x => x.XmlMsgSystemB != null, cancellationToken),
            ConsumedByB: await received.CountAsync(x => x.ConsumedAt != null, cancellationToken),
            AwaitingConsumption: await received.CountAsync(
                x => x.XmlMsgSystemB != null && x.ConsumedAt == null, cancellationToken),
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
                ConsumedByB = g.Sum(x => x.ConsumedAt != null ? 1L : 0L),
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
            DiscrepanciesByField: discrepanciesByField,
            RecentErrors: recentErrors);
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

    private static IQueryable<SpiReceivedMsg> FilterByCreatedAt(IQueryable<SpiReceivedMsg> q, DateTime? from, DateTime? to)
    {
        if (from is not null) q = q.Where(x => x.CreatedAt >= from);
        if (to is not null) q = q.Where(x => x.CreatedAt <= to);
        return q;
    }

    private static IQueryable<SpiSentMsg> FilterByCreatedAt(IQueryable<SpiSentMsg> q, DateTime? from, DateTime? to)
    {
        if (from is not null) q = q.Where(x => x.CreatedAt >= from);
        if (to is not null) q = q.Where(x => x.CreatedAt <= to);
        return q;
    }
}
