# Convivência Pix — Coexistence Layer

A .NET 8 coexistence layer that lets an internally-developed Pix system (**System B**) run in parallel with the current vendor system (**System A**) while System A remains the sole live connection to Bacen. System B's traffic is redirected to a proxy that emulates Bacen's SPI API, so System B can be validated end-to-end without touching production.

---

## Architecture

```
┌─────────────┐     ┌─────────────┐
│  System A   │────▶│    Bacen    │
└──────┬──────┘     └─────────────┘
       │ CDC (Debezium)
       ▼
┌──────────────────────────────────────────────────────────────┐
│                         Kafka                                │
│  spi.systemb.requests    spi.systema.cdc                     │
│  spi.systemb.responses   spi.correlation.events              │
│  spi.comparison.events   spi.discrepancies                   │
│  (+ DLQ topics for each)                                     │
└──────────────────────────────────────────────────────────────┘
       ▲                    │
       │                    ▼
┌─────────────┐     ┌───────────────────┐     ┌──────────────┐
│  System B   │────▶│  spi-proxy-api    │────▶│    Redis     │
└─────────────┘     │  (Bacen emulator) │◀────│  (Pub/Sub +  │
                    └───────────────────┘     │   Cache)     │
                                              └──────┬───────┘
                    ┌───────────────────┐            │
                    │ spi-correlate-    │◀───────────┤
                    │ worker            │            │
                    └───────────────────┘            │
                    ┌───────────────────┐            │
                    │ spi-proxy-worker  │────────────┘
                    └───────────────────┘
                    ┌───────────────────┐
                    │ spi-comparison-   │
                    │ engine            │
                    └───────────────────┘
```

### Request flow

1. **System B** sends an SPI XML message to **spi-proxy-api** (mTLS, ISO 20022 `pacs.008`/`pacs.002`/`pacs.004`, or a `pibr.001` SPI Echo request). The API validates, de-duplicates (idempotency via Redis), extracts the message-type-aware idempotency key, publishes the envelope to `spi.systemb.requests`, and immediately returns `201` with the `PI-ResourceId`(s).
2. **Debezium** captures System A's two tables — `SpiEnvioApiBacen` (messages **sent** to Bacen) and `SpiRecepApiBacen` (messages **received** from Bacen) — and streams both into a single topic `spi.systema.cdc`, matching the production Debezium configuration.
3. **spi-correlate-worker** consumes `spi.systemb.requests` and `spi.systema.cdc`, dispatching each CDC event to the inbound or outbound flow by its Debezium `source.table`. It correlates by the shared Bacen **idempotency key** (`EndToEndId` for pacs.008/pacs.002, `RtrId` for pacs.004):
   - **Outbound** — assembles the `SpiSentMsg` System A/B pacs.008 pair. The first of the two to arrive creates the row; the second completes it.
   - **Inbound** — records System A's response in `SpiReceivedMsg`, looks up the correlated pacs.008 pair, and rewrites the System-A-specific fields to the values **System B expects** (config-driven rules — currently `EndToEndId` and the initiation form `LclInstrm/Prtry`, e.g. `DICT`→`MANU`). It then publishes a ready-for-System-B event to `spi.systemb.responses`.
   - **Echo (`pibr.001`)** — a keepalive System B originates itself, with **no** System A counterpart. The worker recognises it by message type (dispatching before correlation), synthesises the matching `pibr.002` EchoRpt (swaps `Fr`/`To`, mints a fresh `MsgId`, echoes `Data`→`OrgnlData`), and publishes it straight to `spi.systemb.responses` — bypassing correlation entirely.
4. **spi-proxy-worker** consumes `spi.systemb.responses` and signs the payload through the **HSM abstraction only** (`IHsmService`). The Dinamo HSM signs the whole SPI envelope (`SignPIX`) and places the signature in `AppHdr/Sgntr`; it then enqueues the signed XML on a Redis-backed outbound stream.
5. **System B** pulls signed responses via `GET /api/v1/out/{ispb}/stream/start` (long-poll), continues the stream with the returned id, and acks a batch with `DELETE` — aligned with the Bacen SPI ICOM §2.2.2 pull model.
6. **spi-comparison-engine** consumes the correlation/comparison events and writes `SpiDiscrepancy` rows for any field-level differences, feeding the Grafana dashboard.

---

## Solution structure

```
src/
├── Domain/                    # Entities, value objects, repository interfaces
├── Application/               # Use cases, DTOs, application interfaces
├── Infrastructure/            # EF Core, Kafka, Redis, HSM, XML signing, metrics
├── SpiProxyApi/               # ASP.NET Core — Bacen SPI emulator
├── SpiCorrelateWorker/        # Worker — idempotency-key correlation, response transformation, pibr Echo generation
├── SpiProxyWorker/            # Worker — signs transformed responses and enqueues them for System B to pull
└── SpiComparisonEngine/       # Worker — field-level comparison and discrepancy logging

tests/
├── ConvivenciaPix.Domain.Tests/          # Pure unit tests
├── ConvivenciaPix.Application.Tests/    # Use-case unit tests with Moq
├── ConvivenciaPix.Infrastructure.Tests/ # Repository, cache, parser, signing, transformer (Testcontainers)
└── ConvivenciaPix.Integration.Tests/    # Full pipeline E2E (Testcontainers)
```

---

## Prerequisites

| Tool | Version |
|---|---|
| .NET SDK | 8.0+ |
| Docker Desktop | 4.x+ |

---

## Local setup

### 1. Clone and copy environment config

```bash
git clone https://github.com/pix-coexistence/convivencia-pix.git
cd convivencia-pix
cp .env.example .env          # Review and adjust if needed — defaults work for local dev
```

### 2. Start the full infrastructure stack

```bash
make infra-up
```

This starts: SQL Server, Kafka (+ Zookeeper), Redis, Debezium, Kafka UI, Debezium UI, Jaeger, Prometheus, and Grafana.

Wait ~30 s for SQL Server to finish initialising before provisioning the schema.

### 3. Provision the database schema

```bash
make migrate
```

This creates the `DB_COEXISTENCE` database (if missing) and applies every script in `infra/sql/` against it via the `sqlcmd` shipped inside the SQL Server container. The scripts are idempotent — re-running them is safe. To add a new table or index, drop a new numbered file into `infra/sql/` (e.g. `004_…sql`) and re-run `make migrate`; the test fixtures pick it up automatically because the scripts are embedded into the Infrastructure assembly.

### 4. Run the services

Each service is a separate process. Open four terminals:

```bash
# Terminal 1
make run-api

# Terminal 2
dotnet run --project src/SpiCorrelateWorker/ConvivenciaPix.SpiCorrelateWorker.csproj

# Terminal 3
dotnet run --project src/SpiProxyWorker/ConvivenciaPix.SpiProxyWorker.csproj

# Terminal 4
dotnet run --project src/SpiComparisonEngine/ConvivenciaPix.SpiComparisonEngine.csproj
```

The API will be available at `http://localhost:5152` (HTTPS: `https://localhost:7101`). Swagger UI is at `http://localhost:5152/swagger`.

---

## Building

```bash
dotnet build ConvivenciaPix.sln
```

Zero warnings expected. The solution uses Central Package Management (`Directory.Packages.props`) — no per-project version pins needed.

---

## Testing

### Unit tests (no Docker required)

```bash
dotnet test tests/ConvivenciaPix.Domain.Tests/ConvivenciaPix.Domain.Tests.csproj
dotnet test tests/ConvivenciaPix.Application.Tests/ConvivenciaPix.Application.Tests.csproj
```

These cover all domain entities, value objects, and application use cases via Moq mocks. Runs in under 5 seconds.

### Infrastructure tests (Docker required)

```bash
dotnet test tests/ConvivenciaPix.Infrastructure.Tests/ConvivenciaPix.Infrastructure.Tests.csproj
```

Spins up MsSql and Redis containers via Testcontainers to validate repositories, the response cache, the XML parser (including `pibr.001` detection and the message-type-aware idempotency key), the `pibr.002` builder, the enveloped XML signer (signature placed in `AppHdr/Sgntr`), and the Dinamo HSM SDK wrapper.

### Integration / E2E tests (Docker required)

```bash
dotnet test tests/ConvivenciaPix.Integration.Tests/ConvivenciaPix.Integration.Tests.csproj
```

Starts the full stack (Kafka + SQL + Redis containers) and runs `WebApplicationFactory<Program>` with all four workers hosted in-process to exercise every coexistence flow end-to-end: ingest (single/multipart/415/duplicate), the `pibr.001`→signed `pibr.002` Echo, System B / System A outbound correlation and completion, the comparison engine (discrepancy persisted **and** published), Bacen error propagation to System B (RF-04), DLQ routing for uncorrelated events (RF-07), and the pull-stream ack lifecycle.

### All tests

```bash
make test
# or
dotnet test ConvivenciaPix.sln
```

---

## Environment variables

Copy `.env.example` to `.env` and set the values marked `CHANGE_ME` before deploying to non-local environments.

| Variable | Description |
|---|---|
| `ConnectionStrings__SqlServer` | SQL Server connection string |
| `ConnectionStrings__Redis` | Redis connection string |
| `Kafka__BootstrapServers` | Kafka bootstrap address |
| `Dinamo__Host` | HSM hostname (or PFX path in non-Production) |
| `Dinamo__CertId` | HSM signing certificate id (label) — passed to `SignPIX` |
| `Dinamo__KeyId` | HSM signing key id (label) — passed to `SignPIX` |
| `Dinamo__ChainId` | HSM certificate-chain id used by `VerifyPIX` |
| `Correlation__AllowedMessageTypes` | Comma-separated message types the correlate worker processes (default `pacs.002,pacs.004,pacs.008`) |
| `CertificateValidator__TrustedThumbprints__0` | SHA-1 thumbprint of the trusted Bacen client certificate |
| `Kestrel__Certificates__Default__Path` | TLS server certificate PFX path (Production) |
| `Otel__Endpoint` | OpenTelemetry collector gRPC endpoint |

HSM wiring is environment-driven: **Development** uses `MockHsmService` (self-signed dev PFX, no HSM required), **Staging** uses `LocalDinamoSdkClient` (software PFX simulation of the SDK), and **Production** uses `DinamoNetSdkClient` (the real `Dinamo.Hsm` SDK). Response-transformation rules default to code (`ResponseTransformOptions.DefaultRules`) and can be overridden via the optional `ResponseTransform:Rules` config section.

---

## Observability

| Tool | URL | What it shows |
|---|---|---|
| Swagger | `http://localhost:5152/swagger` | SPI Proxy API docs and manual testing |
| Kafka UI | `http://localhost:8080` | Topic lag, message browser |
| Debezium UI | `http://localhost:8084` | CDC connector status |
| Jaeger | `http://localhost:16686` | Distributed traces across all services |
| Prometheus | `http://localhost:9090` | Raw metrics |
| Grafana | `http://localhost:3000` | SPI Overview dashboard (auto-provisioned) |

Grafana credentials: `admin` / `admin` (change on first login).

The pre-provisioned **SPI Overview** dashboard panels:

- DLQ message rate per topic
- Proxy response latency percentiles
- Discrepancy count by field
- Active Kafka consumer lag

---

## Key design decisions

**Idempotency** — every SPI message carries a `MessageId`. The proxy API de-duplicates inbound requests via Redis, and correlation/comparison events are keyed on the `IdempotentId` so they are processed once across consumer groups.

**Correlation** — the correlate worker matches System A and System B by the shared Bacen idempotency key (`EndToEndId` for pacs.008/pacs.002, `RtrId` for pacs.004). The sent-side `SpiSentMsg` pair is assembled first-arrival-creates / second-completes; no external orchestrator or heuristic matching is involved.

**Response transformation** — because System A and System B send independent pacs.008 with per-system field values, Bacen's response (which references System A's values) is rewritten to what System B expects before delivery. Rules are config-driven (`ResponseTransformOptions`), seeded with `EndToEndId` and the initiation form `LclInstrm/Prtry`, and skip any field the response does not carry. If the correlated pacs.008 pair is missing or incomplete, the response is routed to the DLQ rather than delivered untransformed.

**SPI Echo (`pibr.001`)** — Echo requests are originated by System B itself and have no System A transaction to correlate against. The correlate worker dispatches them by message type *before* the correlation gate and generates the matching `pibr.002` EchoRpt directly, reusing the existing sign-and-deliver path — so no new topic, consumer, or signing code is required, and the message never creates a perpetually-incomplete correlation row.

**XML signing** — all signing goes through `IHsmService` only. In Production the Dinamo HSM signs the entire SPI envelope internally (`SignPIX`) and places the signature in `AppHdr/Sgntr`; the software paths (dev/staging) mirror that placement via `EnvelopedXmlSigner`. The former managed `XmlSigningService` (which appended the signature at the document root) was removed.

**Response delivery** — signed responses are enqueued on a Redis-backed outbound stream. System B pulls them with `GET /api/v1/out/{ispb}/stream/start` (long-poll), continues with the returned stream id, and acks a batch via `DELETE` — the Bacen SPI ICOM §2.2.2 pull model.

**mTLS** — in Production, Kestrel requires a client certificate at the TLS layer and `BacenCertificateValidator` enforces thumbprint, expiry, and chain validation. In Development, `DevCertificateValidator` bypasses this check.

**Dead Letter Queues** — every Kafka consumer routes unprocessable messages to a `*.dlq` topic after a configurable retry count. A Prometheus alert fires when DLQ volume exceeds threshold.

**HSM abstraction** — `IDinamoSdkClient` is injected. In Production it calls the Dinamo HSM SDK. In Development/test it uses `LocalDinamoSdkClient` backed by `System.Security.Cryptography` — no hardware required.
