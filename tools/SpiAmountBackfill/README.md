# SpiAmountBackfill

One-off tool that populates the `TransferAmount` / `WithdrawalAmount` columns on existing
`SpiSentMsg` and `SpiReceivedMsg` rows by re-parsing each row's stored message XML with the
same `ISpiXmlParser` the correlate worker uses.

It is **idempotent and re-runnable**: it only touches rows where `TransferAmount IS NULL` and
some XML (`XmlMsgSystemA` or `XmlMsgSystemB`) is present, processing them in cursor-paged
batches. Rows whose XML cannot be parsed are logged and left untouched.

## Run

Apply the schema first (adds the columns) if not already done:

```bash
make migrate
```

Then run the backfill against the same database:

```bash
dotnet run --project tools/SpiAmountBackfill
```

Configuration (`appsettings.json`, environment variables, or command line):

- `ConnectionStrings:SqlServer` — target database (defaults to the local dev container).
- `Backfill:BatchSize` — rows per batch (default 500).

Example overriding the connection string and batch size:

```bash
dotnet run --project tools/SpiAmountBackfill \
  -- --ConnectionStrings:SqlServer="Server=...;Database=DB_COEXISTENCE;..." --Backfill:BatchSize=1000
```

> Note: until the Pix Saque/Troco withdrawal-amount XML element is confirmed in
> `SpiXmlParser.ExtractAmounts`, `WithdrawalAmount` is backfilled as `0` and `TransferAmount`
> carries the full settlement amount. Re-run this tool after that element is wired in.
