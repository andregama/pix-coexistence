# SpiAmountBackfill

One-off tool that repairs the amount / transaction-status columns on the coexistence message tables.

Three passes, all idempotent and re-runnable:

1. **Sent amounts** — reparses each `SpiSentMsg` row's own XML into `TransferAmount` / `WithdrawalAmount`
   (outbound rows are never overwritten, so their stored XML is authoritative).
2. **Received reconstruction** — reads the original messages from **`DB_SYSTEMA.dbo.SpiRecepApiBacen`** and:
   - restores each `(EndToEndId, 'pacs.008')` row's `XmlMsgSystemA` + amounts (an inbound pacs.002 had
     overwritten the credit body in `DB_COEXISTENCE`, so it survives only in System A's table);
   - inserts the **separate** `(EndToEndId, 'pacs.002')` response row with its `TxStatus` and, when
     `TxSts=RJCT`, a `REJECTED_TRANSFER` marker on `SystemAErrorCode`.
3. **Sent TxStatus** — reads outbound pacs.002 acks from **`DB_SYSTEMA.dbo.SpiEnvioApiBacen`** and fills
   `SpiSentMsg.TxStatus` (first-wins), stamping the rejected marker on `SystemAErrorCode` for RJCT.

Passes 2–3 need System A's database and are **skipped with a warning** when `ConnectionStrings:SystemA`
is unset.

## Run

Apply the schema first (composite key + amount/status columns):

```bash
make migrate
```

Then run the backfill:

```bash
dotnet run --project tools/SpiAmountBackfill
```

Configuration (`appsettings.json`, environment variables, or command line):

- `ConnectionStrings:SqlServer` — the coexistence database (`DB_COEXISTENCE`), written to.
- `ConnectionStrings:SystemA` — System A's database (`DB_SYSTEMA`), read-only source for passes 2–3.
- `Backfill:BatchSize` — rows per batch for the sent-amount pass (default 500).

Example overriding connection strings:

```bash
dotnet run --project tools/SpiAmountBackfill \
  -- --ConnectionStrings:SqlServer="Server=...;Database=DB_COEXISTENCE;..." \
     --ConnectionStrings:SystemA="Server=...;Database=DB_SYSTEMA;..."
```
