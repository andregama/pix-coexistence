using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Infrastructure.Parsing;
using ConvivenciaPix.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// One-off backfill: parses the transfer/withdrawal amounts out of each existing row's stored XML
// (SpiSentMsg + SpiReceivedMsg) and writes them into the new amount columns. Idempotent and
// re-runnable — only touches rows where TransferAmount IS NULL and some XML is present. Registers
// just the DbContext + parser (no Kafka/Redis/HSM). Run: dotnet run --project tools/SpiAmountBackfill

// Anchor the content root to the executable directory so appsettings.json is found regardless of the
// caller's working directory (e.g. `dotnet run` from the repo root). Env vars / CLI args still override.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddDbContext<CoexistenceDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("SqlServer")
        ?? throw new InvalidOperationException("ConnectionStrings:SqlServer is required.")));
builder.Services.AddSingleton<ISpiXmlParser, SpiXmlParser>();

var host = builder.Build();

var batchSize = Math.Max(1, builder.Configuration.GetValue("Backfill:BatchSize", 500));

using var scope = host.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<CoexistenceDbContext>();
var parser = scope.ServiceProvider.GetRequiredService<ISpiXmlParser>();
var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SpiAmountBackfill");

var ct = CancellationToken.None;

var received = await BackfillReceivedAsync(db, parser, batchSize, logger, ct);
var sent = await BackfillSentAsync(db, parser, batchSize, logger, ct);

logger.LogInformation("Backfill complete. SpiReceivedMsg updated={Received}, SpiSentMsg updated={Sent}.", received, sent);
return 0;

static async Task<int> BackfillReceivedAsync(
    CoexistenceDbContext db, ISpiXmlParser parser, int batchSize, ILogger logger, CancellationToken ct)
{
    var updated = 0;
    string? cursor = null;
    while (true)
    {
        // Cursor-paged by IdempotentId so unparseable rows (left NULL) never re-appear and the loop
        // always terminates. Tracked entities so SetAmounts mutations persist on SaveChanges.
        var batch = await db.SpiReceivedMsgs
            .Where(x => x.TransferAmount == null
                && (x.XmlMsgSystemA != null || x.XmlMsgSystemB != null)
                && (cursor == null || string.Compare(x.IdempotentId, cursor) > 0))
            .OrderBy(x => x.IdempotentId)
            .Take(batchSize)
            .ToListAsync(ct);
        if (batch.Count == 0)
            break;

        foreach (var row in batch)
        {
            var xml = row.XmlMsgSystemA ?? row.XmlMsgSystemB;
            if (TryExtract(parser, xml!, row.MsgType, row.IdempotentId, logger, out var amounts))
            {
                row.SetAmounts(amounts.Transfer, amounts.Withdrawal);
                updated++;
            }
        }

        cursor = batch[^1].IdempotentId;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        logger.LogInformation("SpiReceivedMsg: processed up to IdempotentId={Cursor}, updated so far={Updated}.", cursor, updated);
    }
    return updated;
}

static async Task<int> BackfillSentAsync(
    CoexistenceDbContext db, ISpiXmlParser parser, int batchSize, ILogger logger, CancellationToken ct)
{
    var updated = 0;
    string? cursor = null;
    while (true)
    {
        var batch = await db.SpiSentMsgs
            .Where(x => x.TransferAmount == null
                && (x.XmlMsgSystemA != null || x.XmlMsgSystemB != null)
                && (cursor == null || string.Compare(x.IdempotentId, cursor) > 0))
            .OrderBy(x => x.IdempotentId)
            .Take(batchSize)
            .ToListAsync(ct);
        if (batch.Count == 0)
            break;

        foreach (var row in batch)
        {
            var xml = row.XmlMsgSystemA ?? row.XmlMsgSystemB;
            if (TryExtract(parser, xml!, row.MsgType, row.IdempotentId, logger, out var amounts))
            {
                row.SetAmounts(amounts.Transfer, amounts.Withdrawal);
                updated++;
            }
        }

        cursor = batch[^1].IdempotentId;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        logger.LogInformation("SpiSentMsg: processed up to IdempotentId={Cursor}, updated so far={Updated}.", cursor, updated);
    }
    return updated;
}

static bool TryExtract(
    ISpiXmlParser parser, string xml, string msgType, string idempotentId,
    ILogger logger, out (decimal Transfer, decimal Withdrawal) amounts)
{
    try
    {
        amounts = parser.ExtractAmounts(xml, msgType);
        return true;
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Skipping row IdempotentId={Id} MsgType={Type}: could not parse amounts.", idempotentId, msgType);
        amounts = default;
        return false;
    }
}
