using ConvivenciaPix.Application.Common;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Infrastructure.Parsing;
using ConvivenciaPix.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// One-off backfill for the amount/status columns.
//   * Sent amounts: reparsed from each SpiSentMsg row's own XML (never overwritten by a response).
//   * Received rows + sent TxStatus: rebuilt from System A's source tables (DB_SYSTEMA), because an
//     inbound pacs.002 overwrote the pacs.008 body in SpiReceivedMsg — the original credit survives
//     only in DB_SYSTEMA.dbo.SpiRecepApiBacen. Reconstructs the split (E2E,'pacs.008') and
//     (E2E,'pacs.002') rows and fills SpiSentMsg.TxStatus from DB_SYSTEMA.dbo.SpiEnvioApiBacen.
// Idempotent and re-runnable. Run: dotnet run --project tools/SpiAmountBackfill

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

var coexistenceCs = builder.Configuration.GetConnectionString("SqlServer")
    ?? throw new InvalidOperationException("ConnectionStrings:SqlServer is required.");
var systemACs = builder.Configuration.GetConnectionString("SystemA");

builder.Services.AddDbContext<CoexistenceDbContext>(options => options.UseSqlServer(coexistenceCs));
builder.Services.AddSingleton<ISpiXmlParser, SpiXmlParser>();

var host = builder.Build();
var batchSize = Math.Max(1, builder.Configuration.GetValue("Backfill:BatchSize", 500));

using var scope = host.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<CoexistenceDbContext>();
var parser = scope.ServiceProvider.GetRequiredService<ISpiXmlParser>();
var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SpiAmountBackfill");
var ct = CancellationToken.None;

var sent = await BackfillSentAmountsAsync(db, parser, batchSize, logger, ct);

var recvRestored = 0;
var recvCreated = 0;
var sentStatus = 0;
if (string.IsNullOrWhiteSpace(systemACs))
{
    logger.LogWarning("ConnectionStrings:SystemA is not set — skipping the System A reconstruction passes "
        + "(received rows and sent TxStatus). Set it to DB_SYSTEMA to recover historical received data.");
}
else
{
    (recvRestored, recvCreated) = await ReconstructReceivedFromSystemAAsync(systemACs, coexistenceCs, parser, logger, ct);
    sentStatus = await BackfillSentTxStatusFromSystemAAsync(systemACs, coexistenceCs, parser, logger, ct);
}

logger.LogInformation(
    "Backfill complete. Sent amounts={Sent}; received pacs.008 restored={RecvRestored}, pacs.002 rows upserted={RecvCreated}; sent TxStatus set={SentStatus}.",
    sent, recvRestored, recvCreated, sentStatus);
return 0;

// --- Sent amounts: reparse each SpiSentMsg row's own XML (local, EF) ---------------------------------
static async Task<int> BackfillSentAmountsAsync(
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
            try
            {
                var (t, w) = parser.ExtractAmounts(xml!, row.MsgType);
                row.SetAmounts(t, w);
                updated++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Skipping SpiSentMsg IdempotentId={Id} MsgType={Type}: could not parse amounts.",
                    row.IdempotentId, row.MsgType);
            }
        }

        cursor = batch[^1].IdempotentId;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }
    return updated;
}

// --- Received: rebuild pacs.008 + pacs.002 rows from DB_SYSTEMA.dbo.SpiRecepApiBacen (raw SQL) --------
static async Task<(int Restored, int Created)> ReconstructReceivedFromSystemAAsync(
    string systemACs, string coexistenceCs, ISpiXmlParser parser, ILogger logger, CancellationToken ct)
{
    const string Pacs008 = "pacs.008";
    const string Pacs002 = "pacs.002";
    var restored = 0;
    var created = 0;

    await using var write = new SqlConnection(coexistenceCs);
    await write.OpenAsync(ct);

    await foreach (var xml in ReadSystemAMessagesAsync(systemACs, "SpiRecepApiBacen", logger, ct))
    {
        string msgType, e2e;
        try
        {
            msgType = parser.ExtractMessageType(xml);
            e2e = parser.ExtractCorrelationKey(xml, msgType);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SpiRecepApiBacen: skipping unparseable message.");
            continue;
        }
        if (string.IsNullOrEmpty(e2e))
            continue;

        if (msgType == Pacs008)
        {
            var (t, w) = parser.ExtractAmounts(xml, msgType);
            // Restore the original credit body + amounts (overwriting any pacs.002 that clobbered it).
            var affected = await ExecAsync(write, ct,
                "UPDATE dbo.SpiReceivedMsg SET XmlMsgSystemA=@xml, TransferAmount=@t, WithdrawalAmount=@w, UpdatedAt=SYSUTCDATETIME() "
                + "WHERE IdempotentId=@e2e AND MsgType=@type",
                ("@xml", xml), ("@t", t), ("@w", w), ("@e2e", e2e), ("@type", Pacs008));
            if (affected == 0)
                await ExecAsync(write, ct,
                    "INSERT INTO dbo.SpiReceivedMsg (IdempotentId, MsgType, XmlMsgSystemA, TransferAmount, WithdrawalAmount, CreatedAt) "
                    + "VALUES (@e2e, @type, @xml, @t, @w, @created)",
                    ("@e2e", e2e), ("@type", Pacs008), ("@xml", xml), ("@t", t), ("@w", w),
                    ("@created", MessageTimestamp(parser, xml)));
            restored++;
        }
        else if (msgType == Pacs002)
        {
            var status = parser.ExtractTransactionStatus(xml);
            // Errors are derived from the message XML: a rejected pacs.002 (TxSts=RJCT) gets the marker.
            var err = status == TxStatuses.Rejected ? SpiErrorCodes.RejectedTransfer : null;
            // Create the response as its own row (it used to overwrite the credit).
            var affected = await ExecAsync(write, ct,
                "UPDATE dbo.SpiReceivedMsg SET XmlMsgSystemA=@xml, SystemAErrorCode=@err, TxStatus=@status, UpdatedAt=SYSUTCDATETIME() "
                + "WHERE IdempotentId=@e2e AND MsgType=@type",
                ("@xml", xml), ("@err", (object?)err ?? DBNull.Value), ("@status", (object?)status ?? DBNull.Value),
                ("@e2e", e2e), ("@type", Pacs002));
            if (affected == 0)
                await ExecAsync(write, ct,
                    "INSERT INTO dbo.SpiReceivedMsg (IdempotentId, MsgType, XmlMsgSystemA, SystemAErrorCode, TxStatus, CreatedAt) "
                    + "VALUES (@e2e, @type, @xml, @err, @status, @created)",
                    ("@e2e", e2e), ("@type", Pacs002), ("@xml", xml), ("@err", (object?)err ?? DBNull.Value),
                    ("@status", (object?)status ?? DBNull.Value), ("@created", MessageTimestamp(parser, xml)));
            created++;
        }

        if ((restored + created) % 500 == 0)
            logger.LogInformation("SpiRecepApiBacen: pacs.008 restored={Restored}, pacs.002 upserted={Created} so far.", restored, created);
    }
    return (restored, created);
}

// --- Sent TxStatus: from DB_SYSTEMA.dbo.SpiEnvioApiBacen outbound pacs.002 (raw SQL) -----------------
static async Task<int> BackfillSentTxStatusFromSystemAAsync(
    string systemACs, string coexistenceCs, ISpiXmlParser parser, ILogger logger, CancellationToken ct)
{
    const string Pacs002 = "pacs.002";
    var updated = 0;

    await using var write = new SqlConnection(coexistenceCs);
    await write.OpenAsync(ct);

    await foreach (var xml in ReadSystemAMessagesAsync(systemACs, "SpiEnvioApiBacen", logger, ct))
    {
        string msgType, e2e;
        try
        {
            msgType = parser.ExtractMessageType(xml);
            if (msgType != Pacs002) continue;
            e2e = parser.ExtractCorrelationKey(xml, msgType);
        }
        catch { continue; }
        if (string.IsNullOrEmpty(e2e))
            continue;

        var status = parser.ExtractTransactionStatus(xml);
        if (status is null)
            continue;
        var marker = status == TxStatuses.Rejected ? SpiErrorCodes.RejectedTransfer : null;

        // First-wins on TxStatus; stamp the rejected marker only when rejected and no error yet.
        var affected = await ExecAsync(write, ct,
            "UPDATE dbo.SpiSentMsg SET TxStatus=@status, "
            + "SystemAErrorCode = CASE WHEN @marker IS NOT NULL AND SystemAErrorCode IS NULL THEN @marker ELSE SystemAErrorCode END, "
            + "UpdatedAt=SYSUTCDATETIME() "
            + "WHERE IdempotentId=@e2e AND MsgType=@type AND TxStatus IS NULL",
            ("@status", status), ("@marker", (object?)marker ?? DBNull.Value), ("@e2e", e2e), ("@type", Pacs002));
        updated += affected;
    }
    return updated;
}

// --- Helpers -----------------------------------------------------------------------------------------
static async IAsyncEnumerable<string> ReadSystemAMessagesAsync(
    string systemACs, string table, ILogger logger,
    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
{
    await using var read = new SqlConnection(systemACs);
    await read.OpenAsync(ct);
    await using var cmd = read.CreateCommand();
    cmd.CommandText = $"SELECT XmlMsg FROM dbo.{table}";
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        if (reader.IsDBNull(0)) continue;
        yield return reader.GetString(0);
    }
}

static async Task<int> ExecAsync(SqlConnection conn, CancellationToken ct, string sql,
    params (string Name, object Value)[] parameters)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters)
        cmd.Parameters.AddWithValue(name, value);
    return await cmd.ExecuteNonQueryAsync(ct);
}

static DateTime MessageTimestamp(ISpiXmlParser parser, string xml)
{
    try { return parser.ExtractTimestamp(xml).UtcDateTime; }
    catch { return DateTime.UtcNow; }
}
