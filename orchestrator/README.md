# Orchestrator — Payer Account resolver

Standalone .NET 8 library that resolves the **payer's branch and account number** from a
pacs.008 / pacs.004 message stored in the coexistence `dbo.SpiSentMsg` table.

> This code is intentionally **separate** from the ConvivenciaPix solution — its own directory,
> its own `Orchestrator.sln`, and its own `Directory.Build.props` (which stops it inheriting the
> solution's build settings / central package versions). It references none of the solution's projects.

## What it does

Given a `SpiSentMsg` `IdempotentId`:

- **pacs.008** — reads the payer account directly from `DbtrAcct`:
  - account number ← `DbtrAcct/Id/Othr/Id`
  - branch (agência) ← `DbtrAcct/Id/Othr/Issr`
- **pacs.004** (return) — the return carries **no** customer account (per the SPI catalog it holds only
  agent ISPBs), so the resolver follows the original payment: it takes the pacs.004's
  `OrgnlEndToEndId` (preferring the stored `OriginalMsgIdempotentId` column, else parsing it from the
  XML), looks up that original **pacs.008** row in `SpiSentMsg`, and reads the account from there.

The XML is taken from **System A** (`XmlMsgSystemA`), falling back to **System B** when A is null.
XPath is namespace-agnostic (`local-name()`), so it works whatever the message's XML namespace.

Returns `null` when the row (or, for a pacs.004, its original pacs.008) is not found in `SpiSentMsg`
or carries no debtor account. Throws `NotSupportedException` for any MsgType other than pacs.008/pacs.004.

## Usage

```csharp
using Orchestrator.PayerAccount;

ISpiSentMsgReader reader = new SpiSentMsgSqlReader(
    "Server=localhost,1433;Database=DB_COEXISTENCE;User Id=sa;Password=***;TrustServerCertificate=True;");
var service = new PayerAccountService(reader, new PayerAccountExtractor());

PayerAccountInfo? payer = await service.GetPayerAccountAsync("E2E-abc123...");
if (payer is not null)
    Console.WriteLine($"Branch {payer.Branch}, Account {payer.Account}");
```

- `PayerAccountExtractor` is pure XML (no I/O) — usable on its own if you already have the XML.
- `ISpiSentMsgReader` abstracts the DB; `SpiSentMsgSqlReader` is a read-only ADO.NET implementation
  (`Microsoft.Data.SqlClient`). Swap in your own reader to integrate with the orchestrator's data access.

## Build & test

```bash
cd orchestrator
dotnet test
```

## Notes

- A pacs.004 whose original pacs.008 was **received** by System A lives in `SpiReceivedMsg`, not
  `SpiSentMsg`; this resolver only reads `SpiSentMsg`, so such a case returns `null`.
