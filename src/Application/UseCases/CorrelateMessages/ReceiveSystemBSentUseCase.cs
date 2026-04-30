using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;

namespace ConvivenciaPix.Application.UseCases.CorrelateMessages;

public sealed class ReceiveSystemBSentUseCase : IReceiveSystemBSentUseCase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISpiXmlParser _xmlParser;
    private readonly ILogger<ReceiveSystemBSentUseCase> _logger;

    public ReceiveSystemBSentUseCase(
        IServiceScopeFactory scopeFactory,
        ISpiXmlParser xmlParser,
        ILogger<ReceiveSystemBSentUseCase> logger)
    {
        _scopeFactory = scopeFactory;
        _xmlParser = xmlParser;
        _logger = logger;
    }

    public async Task ExecuteAsync(KafkaEnvelope envelope, CancellationToken cancellationToken)
    {
        var rawXml = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.PayloadBase64));
        var idSystemB = envelope.CorrelationId
            ?? _xmlParser.ExtractEndToEndId(rawXml);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISpiPendingSystemBMsgRepository>();

        // Idempotency: skip if already stored
        if (await repo.ExistsByIdSystemBAsync(idSystemB, cancellationToken))
        {
            _logger.LogDebug("Pending record already exists for IdSystemB={IdSystemB} — skipping", idSystemB);
            return;
        }

        var timestamp = _xmlParser.ExtractTimestamp(rawXml);
        var amount = _xmlParser.ExtractAmount(rawXml);
        var payerId = _xmlParser.ExtractPayerId(rawXml);
        var payeeId = _xmlParser.ExtractPayeeId(rawXml);

        var pending = SpiPendingSystemBMsg.Create(
            idSystemB, envelope.MessageId, timestamp, amount, payerId, payeeId, rawXml);

        await repo.AddAsync(pending, cancellationToken);
        _logger.LogInformation("Stored pending System B message. IdSystemB={IdSystemB} MessageId={MessageId}", idSystemB, envelope.MessageId);
    }
}
