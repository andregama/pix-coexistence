using ConvivenciaPix.V2.Application.Audit;
using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Application.Parsing;
using ConvivenciaPix.V2.Application.Routing;
using ConvivenciaPix.V2.Domain.Messages;
using ConvivenciaPix.V2.Domain.Repositories;
using ConvivenciaPix.V2.Domain.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConvivenciaPix.V2.Application.UseCases.ProcessInboundMessage;

public interface IProcessInboundMessageUseCase
{
    /// <summary>
    /// Verifies, routes, signs and durably stores one message pulled from Bacen. When this returns,
    /// the message is safe to acknowledge to Bacen. Throws if it could not be signed or stored.
    /// Returns null when nothing was delivered (a redelivery, or a quarantined message).
    /// </summary>
    Task<RoutingDecision?> ExecuteAsync(string piResourceId, byte[] payload, CancellationToken cancellationToken = default);
}

public sealed class ProcessInboundMessageUseCase : IProcessInboundMessageUseCase
{
    private readonly ISpiMessageParser _parser;
    private readonly IInboundRouter _router;
    private readonly ISpiMessageSigner _signer;
    private readonly IInboundMessageSplitter _splitter;
    private readonly IInboundMessageRepository _messages;
    private readonly IDeliveryNotifier _notifier;
    private readonly IProxyMetrics _metrics;
    private readonly InboundSigningOptions _signing;
    private readonly ILogger<ProcessInboundMessageUseCase> _logger;

    public ProcessInboundMessageUseCase(
        ISpiMessageParser parser,
        IInboundRouter router,
        ISpiMessageSigner signer,
        IInboundMessageSplitter splitter,
        IInboundMessageRepository messages,
        IDeliveryNotifier notifier,
        IProxyMetrics metrics,
        IOptions<InboundSigningOptions> signing,
        ILogger<ProcessInboundMessageUseCase> logger)
    {
        _parser = parser;
        _router = router;
        _signer = signer;
        _splitter = splitter;
        _messages = messages;
        _notifier = notifier;
        _metrics = metrics;
        _signing = signing.Value;
        _logger = logger;
    }

    public async Task<RoutingDecision?> ExecuteAsync(
        string piResourceId, byte[] payload, CancellationToken cancellationToken = default)
    {
        // A redelivery from Bacen keeps the decision taken the first time.
        if (await _messages.ExistsAsync(piResourceId, cancellationToken))
        {
            _metrics.InboundDuplicate();
            return null;
        }

        var parsed = _parser.Parse(payload);

        // Everything a system receives carries the bank's signature, which vouches for the content.
        // So content that is not XML (it cannot be signed) or that Bacen's signature does not cover
        // is kept for investigation and delivered to nobody. The stream moves on either way.
        if (parsed.IsUnparseable)
            return await QuarantineAsync(piResourceId, payload, parsed, RouteSources.Unreadable, cancellationToken);
        if (_signing.VerifyBacenSignature && !_signer.Verify(payload))
            return await QuarantineAsync(piResourceId, payload, parsed, RouteSources.InvalidSignature, cancellationToken);

        var decision = await _router.RouteAsync(parsed, cancellationToken);
        var deliveries = BuildDeliveries(payload, parsed, decision);

        var message = ToMessage(piResourceId, payload, parsed, decision.Source, decision.TargetsText);
        var stored = await _messages.PersistAsync(
            message, deliveries, decision.Bindings, AuditEvents.Received(message), cancellationToken);
        if (!stored)
        {
            _metrics.InboundDuplicate();
            return null;
        }

        _metrics.Routed(parsed.MsgType, decision.Source, decision.TargetsText);
        _logger.LogRouted(piResourceId, parsed.MsgType, decision.TargetsText, decision.Source);
        if (parsed.IsUnknown)
            _logger.LogUnknownMessage(piResourceId, decision.TargetsText);
        if (decision.Source is RouteSources.Split or RouteSources.Mixed)
            _logger.LogMixedOwners(piResourceId, parsed.MsgType, parsed.Transactions.Count, decision.Source, decision.TargetsText);

        foreach (var target in decision.Targets)
        {
            try
            {
                await _notifier.NotifyAsync(target, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The message is already in the SQL queue; pull streams find it on their next poll.
                _logger.LogNotifyFailed(ex, target.ToString());
            }
        }

        return decision;
    }

    /// <summary>
    /// The signed content for each target. A system that receives every transaction gets the
    /// original message under the bank's signature (signed once, shared by all such systems); a
    /// system that receives only some gets a message holding just those.
    /// </summary>
    private List<InboundDelivery> BuildDeliveries(byte[] payload, ParsedSpiMessage parsed, RoutingDecision decision)
    {
        var deliveries = new List<InboundDelivery>(decision.Targets.Count);
        byte[]? whole = null;

        foreach (var target in decision.Targets)
        {
            var part = decision.Parts.FirstOrDefault(p => p.System == target);
            var signed = part is null || part.Transactions.Count >= parsed.Transactions.Count
                ? whole ??= _signer.Sign(payload)
                : _signer.Sign(_splitter.Extract(payload, part.Transactions));
            deliveries.Add(new InboundDelivery(target, signed));
        }

        return deliveries;
    }

    private async Task<RoutingDecision?> QuarantineAsync(
        string piResourceId, byte[] payload, ParsedSpiMessage parsed, string reason, CancellationToken cancellationToken)
    {
        var message = ToMessage(piResourceId, payload, parsed, reason, targets: string.Empty);
        if (await _messages.PersistAsync(message, [], [], AuditEvents.Received(message), cancellationToken))
        {
            _metrics.InboundQuarantined(parsed.MsgType, reason);
            _logger.LogQuarantined(piResourceId, parsed.MsgType, reason);
        }
        return null;
    }

    private static SpiInboundMsg ToMessage(
        string piResourceId, byte[] payload, ParsedSpiMessage parsed, string routeSource, string targets) =>
        new(
            piResourceId,
            parsed.MsgId,
            parsed.MsgType,
            parsed.PrimaryTxKey,
            parsed.OriginalMsgRefs.Count > 0 ? parsed.OriginalMsgRefs[0] : null,
            routeSource,
            targets,
            payload);
}

internal static partial class ProcessInboundMessageLogMessages
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Inbound {PiResourceId} ({MsgType}) routed to {Targets} by {RouteSource}")]
    public static partial void LogRouted(this ILogger logger, string piResourceId, string msgType, string targets, string routeSource);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbound {PiResourceId} could not be identified; delivered to {Targets}")]
    public static partial void LogUnknownMessage(this ILogger logger, string piResourceId, string targets);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inbound {PiResourceId} ({MsgType}) carries {Transactions} transactions owned by different systems: {Outcome} to {Targets}")]
    public static partial void LogMixedOwners(this ILogger logger, string piResourceId, string msgType, int transactions, string outcome, string targets);

    [LoggerMessage(Level = LogLevel.Error, Message = "Inbound {PiResourceId} ({MsgType}) quarantined, not delivered to any system: {Reason}")]
    public static partial void LogQuarantined(this ILogger logger, string piResourceId, string msgType, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not notify pull streams of {System}")]
    public static partial void LogNotifyFailed(this ILogger logger, Exception ex, string system);
}
