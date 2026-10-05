using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Infrastructure.Parsing;
using ConvivenciaPix.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// One-off backfill for SpiSentMsg.CustomerDocument / SpiReceivedMsg.CustomerDocument.
// Reparses each row's stored XML with the same rule the correlate worker applies on insert/update
// (ISpiXmlParser.ExtractCustomerDocument): sent rows as sent by the bank, received rows as received.
// System A's XML is read first, System B's when System A's yields nothing. Only rows whose
// CustomerDocument is NULL are read, so it is idempotent and re-runnable.
// Run: dotnet run --project tools/CustomerDocumentBackfill

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

var coexistenceCs = builder.Configuration.GetConnectionString("SqlServer")
    ?? throw new InvalidOperationException("ConnectionStrings:SqlServer is required.");

builder.Services.AddDbContext<CoexistenceDbContext>(options => options.UseSqlServer(coexistenceCs));
builder.Services.AddSingleton<ISpiXmlParser, SpiXmlParser>();

var host = builder.Build();
var batchSize = Math.Max(1, builder.Configuration.GetValue("Backfill:BatchSize", 500));
var dryRun = builder.Configuration.GetValue("Backfill:DryRun", false);

using var scope = host.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<CoexistenceDbContext>();
var parser = scope.ServiceProvider.GetRequiredService<ISpiXmlParser>();
var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("CustomerDocumentBackfill");
var ct = CancellationToken.None;

if (dryRun)
    logger.LogInformation("Dry run: documents are parsed and counted, nothing is written.");

var (sentScanned, sentFilled) = await BackfillSentAsync(db, parser, batchSize, dryRun, logger, ct);
var (recvScanned, recvFilled) = await BackfillReceivedAsync(db, parser, batchSize, dryRun, logger, ct);

logger.LogInformation(
    "Backfill complete{DryRun}. SpiSentMsg: scanned={SentScanned} filled={SentFilled}; SpiReceivedMsg: scanned={RecvScanned} filled={RecvFilled}.",
    dryRun ? " (dry run)" : "", sentScanned, sentFilled, recvScanned, recvFilled);
return 0;

// --- SpiSentMsg: outbound, the bank is the sender ----------------------------------------------
static async Task<(int Scanned, int Filled)> BackfillSentAsync(
    CoexistenceDbContext db, ISpiXmlParser parser, int batchSize, bool dryRun, ILogger logger, CancellationToken ct)
{
    var scanned = 0;
    var filled = 0;
    string? cursor = null;
    while (true)
    {
        var batch = await db.SpiSentMsgs
            .Where(x => x.CustomerDocument == null
                && (x.XmlMsgSystemA != null || x.XmlMsgSystemB != null)
                && (cursor == null || string.Compare(x.IdempotentId, cursor) > 0))
            .OrderBy(x => x.IdempotentId)
            .Take(batchSize)
            .ToListAsync(ct);
        if (batch.Count == 0)
            break;

        foreach (var row in batch)
        {
            var document = Extract(parser, row.XmlMsgSystemA, row.MsgType, sentByBank: true, logger, row.IdempotentId)
                ?? Extract(parser, row.XmlMsgSystemB, row.MsgType, sentByBank: true, logger, row.IdempotentId);
            if (document is null)
                continue;
            row.SetCustomerDocument(document);
            filled++;
        }

        scanned += batch.Count;
        cursor = batch[^1].IdempotentId;
        if (!dryRun)
            await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        logger.LogInformation("SpiSentMsg: scanned={Scanned} filled={Filled}", scanned, filled);
    }
    return (scanned, filled);
}

// --- SpiReceivedMsg: inbound, the bank is the recipient (key: IdempotentId + MsgType) ------------
static async Task<(int Scanned, int Filled)> BackfillReceivedAsync(
    CoexistenceDbContext db, ISpiXmlParser parser, int batchSize, bool dryRun, ILogger logger, CancellationToken ct)
{
    var scanned = 0;
    var filled = 0;
    string? cursorId = null;
    string? cursorType = null;
    while (true)
    {
        var batch = await db.SpiReceivedMsgs
            .Where(x => x.CustomerDocument == null
                && (x.XmlMsgSystemA != null || x.XmlMsgSystemB != null)
                && (cursorId == null
                    || string.Compare(x.IdempotentId, cursorId) > 0
                    || (x.IdempotentId == cursorId && string.Compare(x.MsgType, cursorType) > 0)))
            .OrderBy(x => x.IdempotentId).ThenBy(x => x.MsgType)
            .Take(batchSize)
            .ToListAsync(ct);
        if (batch.Count == 0)
            break;

        foreach (var row in batch)
        {
            var document = Extract(parser, row.XmlMsgSystemA, row.MsgType, sentByBank: false, logger, row.IdempotentId)
                ?? Extract(parser, row.XmlMsgSystemB, row.MsgType, sentByBank: false, logger, row.IdempotentId);
            if (document is null)
                continue;
            row.SetCustomerDocument(document);
            filled++;
        }

        scanned += batch.Count;
        (cursorId, cursorType) = (batch[^1].IdempotentId, batch[^1].MsgType);
        if (!dryRun)
            await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        logger.LogInformation("SpiReceivedMsg: scanned={Scanned} filled={Filled}", scanned, filled);
    }
    return (scanned, filled);
}

static string? Extract(
    ISpiXmlParser parser, string? xml, string rowMsgType, bool sentByBank, ILogger logger, string idempotentId)
{
    if (string.IsNullOrWhiteSpace(xml))
        return null;
    try
    {
        // The XML's own type wins over the row's: historically an inbound pacs.002 overwrote the
        // body of the (E2E, 'pacs.008') received row.
        string msgType;
        try { msgType = parser.ExtractMessageType(xml); }
        catch (InvalidOperationException) { msgType = rowMsgType; }
        return parser.ExtractCustomerDocument(xml, msgType, sentByBank);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Skipping IdempotentId={Id} MsgType={Type}: could not parse the XML.", idempotentId, rowMsgType);
        return null;
    }
}
