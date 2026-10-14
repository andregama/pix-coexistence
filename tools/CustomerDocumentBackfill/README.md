# CustomerDocumentBackfill

One-off tool that fills `CustomerDocument` (the bank customer's CPF/CNPJ) on the existing rows of
`SpiSentMsg` and `SpiReceivedMsg`, from the XML already stored in `XmlMsgSystemA` / `XmlMsgSystemB`.
New rows get the column from the correlate worker; this covers what was written before.

It uses the same rule as the worker (`ISpiXmlParser.ExtractCustomerDocument`):

- **pacs.008**: the debtor (`Dbtr`) on `SpiSentMsg`, the creditor (`Cdtr`) on `SpiReceivedMsg`
  (first `CdtTrfTxInf`).
- **other types** (pain.009/011/012/013/014, …): the party whose agent (`DbtrAgt`/`CdtrAgt`
  `ClrSysMmbId/MmbId`) is the bank, i.e. the ISPB in `AppHdr/Fr` for sent messages and `AppHdr/To`
  for received ones.
- The document is `<party>/Id/PrvtId|OrgId/Othr/Id`, punctuation removed and upper-cased. Values
  longer than 14 characters are not a CPF/CNPJ and are left out.

System A's XML is read first, System B's when System A's has no document. Messages that name no
customer (pacs.002, pacs.004, admi.*, pibr.*, …) stay `NULL`.

Only rows with `CustomerDocument IS NULL` are read, so the tool is idempotent, re-runnable, and safe
to run while the workers are up.

## Run

Apply the schema first (`infra/sql/011_AddCustomerDocument.sql`, a no-op where the column exists):

```bash
make migrate
```

Then:

```bash
dotnet run --project tools/CustomerDocumentBackfill
```

Configuration (`appsettings.json`, environment variables, or command line):

- `ConnectionStrings:SqlServer`: the coexistence database (`DB_COEXISTENCE`), read and written.
- `Backfill:BatchSize`: rows per batch (default 500).
- `Backfill:DryRun`: `true` parses and counts without writing (default `false`).

```bash
dotnet run --project tools/CustomerDocumentBackfill \
  -- --ConnectionStrings:SqlServer="Server=...;Database=DB_COEXISTENCE;..." --Backfill:DryRun=true
```
