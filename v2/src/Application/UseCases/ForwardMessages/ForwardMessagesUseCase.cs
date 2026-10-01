using ConvivenciaPix.V2.Application.Audit;
using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Application.Parsing;
using ConvivenciaPix.V2.Application.Routing;
using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Domain.Messages;
using ConvivenciaPix.V2.Domain.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConvivenciaPix.V2.Application.UseCases.ForwardMessages;

public sealed class ForwardOptions
{
    /// <summary>How long to wait for the real SPI before answering 504 to the calling system.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// A request received on a system's listener. <paramref name="Body"/> is forwarded untouched (so
/// the sender's signatures stay valid); <paramref name="Messages"/> are the individual XML messages
/// inside it, used only to record who sent what.
/// </summary>
public sealed record ForwardRequest(
    SystemName System,
    string Ispb,
    string ContentType,
    byte[] Body,
    IReadOnlyList<ReadOnlyMemory<byte>> Messages,
    IReadOnlyDictionary<string, string> Headers);

public sealed record ForwardResult(
    int StatusCode,
    IReadOnlyDictionary<string, string[]> Headers,
    string? ContentType,
    byte[] Body)
{
    public static ForwardResult Status(int statusCode) =>
        new(statusCode, new Dictionary<string, string[]>(), null, []);
}

public interface IForwardMessagesUseCase
{
    Task<ForwardResult> ExecuteAsync(ForwardRequest request, CancellationToken cancellationToken = default);
}

public sealed class ForwardMessagesUseCase : IForwardMessagesUseCase
{
    private const string SentOrigin = "Sent";

    private readonly ISpiMessageParser _parser;
    private readonly ISentRouteRepository _sentRoutes;
    private readonly ITxAffinityRepository _affinities;
    private readonly IAuditOutboxRepository _outbox;
    private readonly ISpiGateway _gateway;
    private readonly IProxyMetrics _metrics;
    private readonly ForwardOptions _options;
    private readonly ILogger<ForwardMessagesUseCase> _logger;

    public ForwardMessagesUseCase(
        ISpiMessageParser parser,
        ISentRouteRepository sentRoutes,
        ITxAffinityRepository affinities,
        IAuditOutboxRepository outbox,
        ISpiGateway gateway,
        IProxyMetrics metrics,
        IOptions<ForwardOptions> options,
        ILogger<ForwardMessagesUseCase> logger)
    {
        _parser = parser;
        _sentRoutes = sentRoutes;
        _affinities = affinities;
        _outbox = outbox;
        _gateway = gateway;
        _metrics = metrics;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ForwardResult> ExecuteAsync(ForwardRequest request, CancellationToken cancellationToken = default)
    {
        var parsed = request.Messages.Select(_parser.Parse).ToList();

        // The route must exist before Bacen sees the message: otherwise its response could arrive
        // before we know who to deliver it to. If we cannot record it, we do not forward.
        try
        {
            foreach (var message in parsed)
                await RegisterAsync(request.System, message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogRouteRegistrationFailed(ex, request.System);
            return ForwardResult.Status(503);
        }

        SpiGatewayResponse response;
        try
        {
            response = await _gateway.SendAsync(
                new SpiGatewayRequest(
                    "POST",
                    $"/api/v1/in/{request.Ispb}/msgs",
                    request.Headers,
                    request.ContentType,
                    request.Body,
                    _options.TimeoutSeconds),
                cancellationToken);
        }
        catch (SpiGatewayTimeoutException ex)
        {
            _logger.LogForwardFailed(ex, request.System, 504);
            _metrics.Forwarded(request.System, 504);
            return ForwardResult.Status(504);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogForwardFailed(ex, request.System, 502);
            _metrics.Forwarded(request.System, 502);
            return ForwardResult.Status(502);
        }

        _metrics.Forwarded(request.System, response.StatusCode);
        await RecordOutcomeAsync(request.System, parsed, response, cancellationToken);

        return new ForwardResult(response.StatusCode, response.Headers, response.ContentType, response.Body);
    }

    private async Task RegisterAsync(SystemName system, ParsedSpiMessage message, CancellationToken cancellationToken)
    {
        if (message.MsgId is null)
        {
            // Nothing to key a route on; Bacen will reject the message and the error goes back inline.
            _logger.LogUnidentifiedMessage(system, message.MsgType);
            return;
        }

        var echoHash = message.MsgType == "pibr.001" && !string.IsNullOrEmpty(message.EchoData)
            ? EchoHash.Compute(message.EchoData)
            : null;

        var owner = await _sentRoutes.RegisterAsync(
            new SpiSentRoute(message.MsgId, system, message.MsgType, message.PrimaryTxKey, echoHash),
            cancellationToken);

        if (owner != system)
        {
            // The same MsgId was already sent by the other system. The first sender keeps the route.
            _metrics.SentConflict(system, message.MsgType);
            _logger.LogSentConflict(message.MsgId, system, owner);
            return;
        }

        foreach (var key in message.TxKeys)
            await _affinities.GetOrAddAsync(key, system, SentOrigin, cancellationToken);
    }

    // The message has already reached Bacen: failing to record the outcome must not turn a
    // successful send into an error for the calling system.
    private async Task RecordOutcomeAsync(
        SystemName system, List<ParsedSpiMessage> parsed, SpiGatewayResponse response, CancellationToken cancellationToken)
    {
        var resourceIds = response.Header("PI-ResourceId")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

        try
        {
            for (var i = 0; i < parsed.Count; i++)
            {
                var message = parsed[i];
                if (message.MsgId is null) continue;

                var resourceId = resourceIds.Length == parsed.Count ? resourceIds[i] : null;
                await _sentRoutes.MarkForwardedAsync(message.MsgId, resourceId, response.StatusCode, cancellationToken);
                await _outbox.AddAsync(
                    AuditEvents.Sent(
                        message.MsgId, message.MsgType, system, message.PrimaryTxKey, resourceId, response.StatusCode),
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogOutcomeNotRecorded(ex, system);
        }
    }
}

internal static partial class ForwardMessagesLogMessages
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Could not record the sent route for {System}; request not forwarded")]
    public static partial void LogRouteRegistrationFailed(this ILogger logger, Exception ex, SystemName system);

    [LoggerMessage(Level = LogLevel.Error, Message = "Forward to the SPI failed for {System}; answering {StatusCode}")]
    public static partial void LogForwardFailed(this ILogger logger, Exception ex, SystemName system, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message from {System} of type {MsgType} has no message id; forwarded without a route")]
    public static partial void LogUnidentifiedMessage(this ILogger logger, SystemName system, string msgType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MsgId {MsgId} sent by {System} is already owned by {Owner}; responses will go to {Owner}")]
    public static partial void LogSentConflict(this ILogger logger, string msgId, SystemName system, SystemName owner);

    [LoggerMessage(Level = LogLevel.Error, Message = "Message from {System} was forwarded but its outcome could not be recorded")]
    public static partial void LogOutcomeNotRecorded(this ILogger logger, Exception ex, SystemName system);
}
