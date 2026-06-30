using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.Mappers;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using Microsoft.Extensions.Logging;

namespace ConvivenciaPix.Application.UseCases.CorrelateMessages;

public sealed class CorrelateSystemAInboundUseCase : ICorrelateSystemAInboundUseCase
{
    private readonly ISpiReceivedMsgRepository _receivedMsgRepo;
    private readonly ISpiXmlParser _xmlParser;
    private readonly ILogger<CorrelateSystemAInboundUseCase> _logger;

    public CorrelateSystemAInboundUseCase(
        ISpiReceivedMsgRepository receivedMsgRepo,
        ISpiXmlParser xmlParser,
        ILogger<CorrelateSystemAInboundUseCase> logger)
    {
        _receivedMsgRepo = receivedMsgRepo;
        _xmlParser = xmlParser;
        _logger = logger;
    }

    public async Task ExecuteAsync(string rawCdcJson, IReadOnlySet<string> allowedTypes, CancellationToken ct)
    {
        var mapped = SystemAInboundMapper.Map(rawCdcJson);
        var msgType = _xmlParser.ExtractMessageType(mapped.XmlMsg);

        if (!allowedTypes.Contains(msgType))
        {
            _logger.LogDebug("SystemA inbound: skipping unsupported MsgType={MsgType}", msgType);
            return;
        }

        var idempotentId = _xmlParser.ExtractIdempotentId(mapped.XmlMsg, msgType);
        var originalId = _xmlParser.ExtractOriginalIdempotentId(mapped.XmlMsg, msgType);
        string? msgId = null;
        try { msgId = _xmlParser.ExtractMessageId(mapped.XmlMsg); }
        catch { /* non-critical */ }

        var existing = await _receivedMsgRepo.FindByIdempotentIdAsync(idempotentId, ct);
        if (existing is null)
        {
            var created = SpiReceivedMsg.CreateFromSystemA(
                idempotentId, msgType, msgId, mapped.XmlMsg, mapped.Problem, originalId);
            await _receivedMsgRepo.AddAsync(created, ct);
        }
        else
        {
            existing.SetMsgIdIfAbsent(msgId);
            existing.UpdateFromSystemA(mapped.XmlMsg, mapped.Problem);
            await _receivedMsgRepo.UpdateAsync(existing, ct);
        }

        _logger.LogInformation(
            "SystemA inbound correlated. IdempotentId={Id} MsgType={Type}", idempotentId, msgType);
    }
}
