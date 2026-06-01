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
│  spi.systema.responses   spi.systemb.requests                │
│  spi.correlation.events  spi.systemb.responses               │
│  spi.comparison.events   (+ DLQ topics for each)             │
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

1. **System B** sends an SPI XML message to **spi-proxy-api** (mTLS, ISO 20022 pacs.008).
2. The API validates, de-duplicates (idempotency via Redis), and publishes the envelope to `spi.systemb.requests`.
3. **spi-proxy-api** subscribes to a Redis Pub/Sub channel keyed on System B's `EndToEndId` and waits (default 30 s).
4. **Debezium** captures System A's outbox table and streams CDC events to `spi.systema.responses`.
5. **spi-correlate-worker** consumes both streams and maps System A ↔ System B IDs using two strategies in priority order:
   - **Orchestrator** — queries the orchestrator service for the authoritative link.
   - **Heuristic** — matches on timestamp window, amount, payer, and payee when the orchestrator has no record.
   The correlation source is persisted so the accuracy of the fallback can be monitored.
6. **spi-proxy-worker** picks up the correlated System A response, signs the XML via the HSM abstraction, deposits it in Redis, and signals the waiting API via Pub/Sub.
7. **spi-proxy-api** wakes up, caches the signed response for 24 h, and returns it to System B.
8. **spi-comparison-engine** consumes both sides and writes `SpiDiscrepancy` rows for any field-level differences, feeding the Grafana dashboard.

---

## Solution structure

```
src/
├── Domain/                    # Entities, value objects, repository interfaces
├── Application/               # Use cases, DTOs, application interfaces
├── Infrastructure/            # EF Core, Kafka, Redis, HSM, XML signing, metrics
├── SpiProxyApi/               # ASP.NET Core — Bacen SPI emulator
├── SpiCorrelateWorker/        # Worker — ID correlation (orchestrator + heuristic)
├── SpiProxyWorker/            # Worker — signs and delivers responses to System B
└── SpiComparisonEngine/       # Worker — field-level comparison and discrepancy logging

tests/
├── ConvivenciaPix.Domain.Tests/          # Pure unit tests (34 tests)
├── ConvivenciaPix.Application.Tests/    # Use-case unit tests with Moq (18 tests)
├── ConvivenciaPix.Infrastructure.Tests/ # Repository, cache, parser, signing (Testcontainers)
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

Spins up MsSql and Redis containers via Testcontainers to validate repositories, the response cache, the XML parser, and the XML signing service.

### Integration / E2E tests (Docker required)

```bash
dotnet test tests/ConvivenciaPix.Integration.Tests/ConvivenciaPix.Integration.Tests.csproj
```

Starts the full stack (Kafka + SQL + Redis containers) and runs `WebApplicationFactory<Program>` to exercise the complete request pipeline, including correlation and response delivery.

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
| `Dinamo__CertificateLabel` | HSM certificate label for SPI signing |
| `Orchestrator__BaseUrl` | Internal orchestrator service URL |
| `Orchestrator__ApiKey` | Orchestrator API key |
| `CertificateValidator__TrustedThumbprints__0` | SHA-1 thumbprint of the trusted Bacen client certificate |
| `Kestrel__Certificates__Default__Path` | TLS server certificate PFX path (Production) |
| `Otel__Endpoint` | OpenTelemetry collector gRPC endpoint |

In Development, `LocalDinamoSdkClient` and `StubOrchestratorClient` are injected automatically — no HSM or live orchestrator is required.

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

- Correlation source mix (Orchestrator vs Heuristic ratio)
- DLQ message rate per topic
- Proxy response latency percentiles
- Discrepancy count by field
- Active Kafka consumer lag

---

## Key design decisions

**Idempotency** — every SPI message carries a `MessageId`. The proxy API stores responses keyed on `MessageId` in Redis (TTL 24 h) and returns the cached value on any retry without re-processing.

**Correlation** — the correlate worker tries the orchestrator first, then falls back to heuristic matching (timestamp within a configurable window, amount, payer/payee). The source is stored in `SpiSentMsg.CorrelationSource` so the heuristic fallback rate can be tracked.

**Response delivery** — instead of polling, the proxy API waits on a Redis Pub/Sub channel keyed on the System B `EndToEndId`. The proxy worker signals that channel once the signed response is ready, giving sub-millisecond wake-up.

**mTLS** — in Production, Kestrel requires a client certificate at the TLS layer and `BacenCertificateValidator` enforces thumbprint, expiry, and chain validation. In Development, `DevCertificateValidator` bypasses this check.

**Dead Letter Queues** — every Kafka consumer routes unprocessable messages to a `*.dlq` topic after a configurable retry count. A Prometheus alert fires when DLQ volume exceeds threshold.

**HSM abstraction** — `IDinamoSdkClient` is injected. In Production it calls the Dinamo HSM SDK. In Development/test it uses `LocalDinamoSdkClient` backed by `System.Security.Cryptography` — no hardware required.
