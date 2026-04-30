# Development Plan — Pix Coexistence Layer

## Overview

This document describes the full development plan for the **ConvivenciaPix** solution — a coexistence layer that allows System B (internal Pix implementation) to run in parallel with System A (proprietary vendor system) for homologation, while System A remains the sole connection point to Brazil's Central Bank (Bacen) SPI APIs.

The solution is built on **.NET 8**, **Clean Architecture / DDD**, **Kafka**, **SQL Server**, and **Redis**.

---

## Phase 1 — Infrastructure Scaffold ✅ Complete

**Goal:** Establish the architectural skeleton, domain model, and all infrastructure adapters so that Phase 2 business logic can be implemented without any structural decisions pending.

### Deliverables

#### Domain Layer (`src/Domain`)
- `SpiSentMsg` entity — aggregate representing the ID mapping between System A and System B transactions, enforced via a `Create()` factory with validation
- `CorrelationSource` value object — typed enum with `Orchestrator` and `Heuristic` variants; immutable, comparable
- `SpiDiscrepancyDetected` domain event — carries field-level discrepancies detected by the comparison engine
- `ISpiSentMsgRepository` interface — `FindByIdSystemA`, `FindByIdSystemB`, `ExistsAsync`, `AddAsync`, `DeleteOlderThanAsync`

#### Application Layer (`src/Application`)
- `KafkaEnvelope` DTO — canonical Kafka message wrapper with `MessageId`, `PayloadBase64`, `Timestamp`, `CorrelationId`, `SchemaVersion`
- `SpiRequestDto` — inbound request from System B to the proxy
- `SpiResponseDto` — outbound response from System A (mapped from Debezium CDC)
- `IHsmService` — abstraction over the Dinamo HSM hardware
- `IKafkaPublisher` — single `PublishAsync(topic, envelope, ct)` port
- `IResponseCache` — Redis abstraction with separate `response:` and `idempotency:` key namespaces
- `IXmlSigningService` — `SignAsync` / `VerifyAsync` over `XDocument` + `X509Certificate2`

#### Infrastructure Layer (`src/Infrastructure`)
- **Persistence:** `CoexistenceDbContext`, `SpiSentMsgConfiguration` (composite PK clustered index + two non-clustered unique indexes per spec RF-06), `SpiSentMsgRepository`
- **Cache:** `RedisResponseCache` — NX (set-if-not-exists) for idempotency keys to prevent overwriting processed responses; debug-level cache miss logging for eviction monitoring
- **Messaging:** `KafkaConsumerBase<TKey,TValue>` — abstract `BackgroundService` with automatic DLQ routing on any unhandled exception, diagnostic headers (source topic/partition/offset, exception type/message, timestamp)
- **Messaging:** `KafkaPublisher` — idempotent Kafka producer (`Acks.All`, `EnableIdempotence`, `MessageSendMaxRetries = 5`); propagates `CorrelationId` as a Kafka message header
- **Messaging:** `Topics` — all topic name constants with `DlqFor(topic)` helper
- **Messaging:** `SystemAOutboxMapper` — translates raw Debezium CDC JSON to `SpiResponseDto`; versioned (`MapV1`) so schema changes add `MapV2` without breaking existing logic
- **Signing:** `XmlSigningService` — RSA/SHA-256 enveloped XML Digital Signature using `System.Security.Cryptography.Xml`; C14N transform
- **Signing:** `MockHsmService` — generates a self-signed RSA-2048 PFX at startup; guarded to throw outside `Development` environment
- **Jobs:** `SpiSentMsgCleanupJob` — Quartz `IJob` with `[DisallowConcurrentExecution]`, 30-day TTL, daily at 02:00 UTC
- **DI:** `InfrastructureServiceExtensions.AddInfrastructure()` — single entry point for all adapter registrations

#### Local Development Infrastructure
- `docker-compose.yml` — SQL Server 2022, Zookeeper, Kafka, Kafka UI, Redis, Debezium Connect, Debezium UI, Jaeger (distributed tracing); all services health-checked with dependency ordering
- Kafka topics pre-created (auto-create disabled): `spi.systemb.requests`, `spi.systema.responses`, `spi.systemb.responses`, `spi.correlation.events`, `spi.comparison.events`, `spi.discrepancies` — each paired with a `.dlq` topic
- `infra/debezium/connectors/systema-outbox.json` — SQL Server CDC connector watching `DB_SYSTEMA.dbo.SpiOutbox`, routed to `spi.systema.responses` via the Outbox Event Router transform
- `.env.example` — documents all required environment variables
- `Directory.Build.props` — `TreatWarningsAsErrors=true`, nullable enabled, solution-wide
- `Directory.Packages.props` — Central Package Management for all NuGet dependencies

#### Service Entry Points (stubs only in Phase 1)
- `SpiProxyApi` — ASP.NET Core Web API entry point; no controllers yet
- `SpiProxyWorker`, `SpiCorrelateWorker`, `SpiComparisonEngine` — Worker Service entry points with empty `BackgroundService` stubs

---

## Phase 2 — Business Logic Implementation ✅ Complete

**Goal:** Wire all infrastructure adapters together with the actual message-processing logic, making the coexistence layer functionally complete end-to-end.

### Deliverables

#### New Domain Entities
- `SpiPendingSystemBMsg` entity — stores unmatched System B messages awaiting heuristic correlation. Fields: `Id` (GUID PK), `IdSystemB`, `MessageId`, `Timestamp`, `Amount`, `PayerId`, `PayeeId`, `RawXmlBase64`, `CreatedAt`. Encodes raw XML as Base64 on construction; exposes `DecodeRawXml()`.
- `ISpiPendingSystemBMsgRepository` — `ExistsByIdSystemBAsync`, `AddAsync`, `FindHeuristicMatchAsync`, `DeleteAsync`, `DeleteOlderThanAsync`

#### New Application Ports
- `SpiComparisonEventDto` — `(IdSystemA, IdSystemB, SystemAXml, SystemBXml, CorrelationSource, OccurredAt)`
- `IOrchestratorClient` — `Task<OrchestratorResult?> FindCorrelationAsync(idSystemA, ct)`; returns `null` when not found, triggering heuristic fallback
- `ISpiXmlParser` — `ExtractMessageId`, `ExtractEndToEndId`, `ExtractAmount`, `ExtractPayerId`, `ExtractPayeeId`, `ExtractTimestamp`

#### New Infrastructure Adapters
- **`SpiXmlParser`** — namespace-aware XPath parser for ISO 20022 SPI messages (pacs.008 / pacs.004). Extracts `<MsgId>`, `<EndToEndId>`, `<IntrBkSttlmAmt>`, `<Dbtr>`, `<Cdtr>`, `<IntrBkSttlmDt>`. Supports multiple XPath fallbacks per field; silently skips invalid XPath expressions.
- **`StubOrchestratorClient`** — always returns `null` (forces heuristic path). Documents exactly where the real HTTP call to the orchestrator API must be implemented in production.
- **`SpiPendingSystemBMsgRepository`** — EF Core implementation. Heuristic match query filters by timestamp window (`Timestamp >= lower AND Timestamp <= upper`), exact amount, PayerId, and PayeeId. Returns oldest match (`OrderBy(CreatedAt)`) to minimize stale records.
- **`SpiPendingSystemBMsgConfiguration`** — EF Fluent API: unique index on `IdSystemB`; composite index on `(Timestamp, Amount)` to accelerate heuristic queries.
- **`CoexistenceDbContextFactory`** — `IDesignTimeDbContextFactory` for EF CLI tooling; not used at runtime.

#### EF Core Migration (`InitialCreate`)
Creates both tables in `DB_COEXISTENCE`:
- `SpiSentMsg` with composite clustered PK `(IdSystemA, IdSystemB)` and unique non-clustered indexes on each column individually
- `SpiPendingSystemBMsg` with GUID PK, unique index on `IdSystemB`, composite index on `(Timestamp, Amount)`

#### SpiProxyApi — `POST /api/spi/messages`

Full SPI endpoint emulation for System B. Implements RF-01 (Bacen emulation), RF-03 (idempotency), RF-09 (caching and configurable timeout).

**Request flow:**
1. Read raw XML body; parse `MessageId` and `EndToEndId` (= `IdSystemB`) via `ISpiXmlParser`
2. Idempotency check — if `idempotency:{MessageId}` exists in Redis, return cached response immediately without republishing to Kafka
3. Store raw XML in Redis at `request:{IdSystemB}` (1h TTL) so the proxy worker can include it in the comparison event
4. Publish `KafkaEnvelope` to `spi.systemb.requests` with `CorrelationId = IdSystemB`
5. Poll Redis at `response:{IdSystemB}` every 500ms until response is available or timeout expires
6. On response: store in idempotency cache (24h TTL), return `Content(xml, "application/xml")`
7. On timeout: return HTTP 504 with a Bacen-compliant `FIToFIPmtStsRpt` error XML (reason code `SPI9999`)

**Additional wiring:**
- Health checks: `/healthz` (liveness), `/healthz/ready` (readiness with SQL Server + Redis probes)
- OpenTelemetry tracing with ASP.NET Core instrumentation and OTLP export (configurable endpoint, defaults to Jaeger at `localhost:4317`)
- `ProxyApiOptions` bound from `appsettings.json` section `ProxyApi` (currently `TimeoutSeconds: 30`)

#### SpiCorrelateWorker — Two Kafka Consumers (RF-02, RF-05)

**`SystemBSentConsumer`** — consumer group `spi-correlate-systemb`, topic `spi.systemb.requests`:
- Deserializes `KafkaEnvelope` → base64-decodes → extracts SPI XML fields
- Idempotency: skips if `IdSystemB` already exists in `SpiPendingSystemBMsg`
- Creates and persists `SpiPendingSystemBMsg` for later heuristic matching

**`SystemAResponseCorrelateConsumer`** — consumer group `spi-correlate-systema`, topic `spi.systema.responses`:
- Maps Debezium CDC JSON via `SystemAOutboxMapper.MapV1` to get `IdSystemA`
- Idempotency: skips if `SpiSentMsg` already exists for this `IdSystemA`
- **Primary strategy:** calls `IOrchestratorClient.FindCorrelationAsync(idSystemA)` — if result returned, creates `SpiSentMsg` with `CorrelationSource.Orchestrator`
- **Fallback strategy:** parses XML for timestamp/amount/PayerId/PayeeId; calls `FindHeuristicMatchAsync` against pending records within configurable `HeuristicWindowSeconds` (default 60s) — if match found, creates `SpiSentMsg` with `CorrelationSource.Heuristic`; deletes matched pending record
- If no match: throws `InvalidOperationException` → base class routes to DLQ (`spi.systema.responses.dlq`); message can be replayed after the System B pending record is confirmed to exist
- Publishes correlation event to `spi.correlation.events` with `{IdSystemA, IdSystemB, CorrelationSource, CorrelatedAt}`
- Logs `CorrelationSource` at `Information` level with structured fields for strategy accuracy monitoring (RF-05)

#### SpiProxyWorker — `SystemAResponseProxyConsumer` (RF-04, RF-09)

Consumer group `spi-proxy-systema`, topic `spi.systema.responses`:

1. Maps CDC JSON via `SystemAOutboxMapper.MapV1`
2. **Retry loop (5× with 1s backoff):** calls `ISpiSentMsgRepository.FindByIdSystemAAsync` to get `IdSystemB`. Retries handle the race condition where the correlate worker's DB write hasn't committed before the proxy worker needs the mapping.
3. Retrieves System B's original request XML from Redis at `request:{IdSystemB}`
4. Fetches signing certificate via `IHsmService.GetSigningCertificateAsync`
5. Signs System A's XML via `IXmlSigningService.SignAsync` (RSA/SHA-256 enveloped signature)
6. Deposits signed XML in Redis at `response:{IdSystemB}` (30min TTL) — this unblocks the API's polling loop
7. Publishes signed XML envelope to `spi.systemb.responses`
8. Builds `SpiComparisonEventDto` with both XMLs and publishes to `spi.comparison.events`

Error propagation (RF-04): System A error responses (detected via `IsError` flag on `SpiResponseDto`) are signed and deposited in Redis exactly like success responses — System B receives the same error.

#### SpiComparisonEngine — `SpiComparisonConsumer` (RF-08)

Consumer group `spi-comparison`, topic `spi.comparison.events`:

- Deserializes `KafkaEnvelope` → `SpiComparisonEventDto`
- Parses both System A and System B XMLs via `ISpiXmlParser`
- Compares business fields: **Amount**, **PayerId**, **PayeeId** (ignores transaction-specific identifiers: `MessageId`, `EndToEndId`)
- Logs comparison result at `Debug` level for every message (allows aggregate miss-rate analysis)
- If any field differs: serializes `SpiDiscrepancyDetected` domain event and publishes to `spi.discrepancies` topic at `Warning` log level

#### Bug Fix
`IKafkaPublisher` promoted from `AddScoped` to `AddSingleton`. The previous scoped registration was a lifetime bug: when a DI scope was disposed, it would dispose the `KafkaPublisher` which called `_producer.Dispose()` on the shared singleton `IProducer<string,string>`, breaking all subsequent publish calls.

---

## Phase 3 — Security Hardening ✅ Complete

**Goal:** Harden the solution for production deployment with mutual TLS, real HSM integration, and the orchestrator HTTP client.

### Deliverables

#### mTLS for SpiProxyApi (RF-01)
- `ICertificateValidator` interface in Application layer
- `BacenCertificateValidator` (production) — rejects on TLS policy errors, expired cert, or untrusted thumbprint. Trusted thumbprints configured via `CertificateValidator:TrustedThumbprints[]`.
- `DevCertificateValidator` (development) — always passes, logs a `Warning` on every call
- Kestrel `ClientCertificateMode.RequireCertificate` enabled in non-Development via `WebHost.ConfigureKestrel`
- ASP.NET Core certificate authentication middleware (`Microsoft.AspNetCore.Authentication.Certificate`) validates cert via `ICertificateValidator` in `OnCertificateValidated`
- `[Authorize]` on `SpiController` — unauthenticated requests return `401`
- `appsettings.Production.json` configures Kestrel HTTPS endpoint with `RequireCertificate`

#### Dinamo HSM Two-Layer Abstraction
- `IDinamoSdkClient` interface mirrors the DinamoAPI.NET (DNET) contract exactly: `Connect`, `GetCertificate`, `Sign`, `Disconnect`
- `LocalDinamoSdkClient` — .NET BCL implementation (no DinamoAPI.dll). `Host` is treated as a PFX file path. Used in Development and Staging.
- `DinamoHsmService` — production `IHsmService` backed by `IDinamoSdkClient`. Connects/disconnects per operation (thread safe). Caches the signing certificate after first load.
- In production: ops team adds `DinamoAPI.dll` and registers `DinamoNetSdkClient` (not in this repo) implementing `IDinamoSdkClient`. No changes to `DinamoHsmService` required.
- `DinamoOptions` bound from `Dinamo` config section: `Host`, `Port` (4433), `UserId`, `Password`, `CertificateLabel`, `KeyLabel`, `SignMechanism` (`RSA_PKCS1_V1_5`)

#### Orchestrator HTTP Client with Resilience
- `HttpOrchestratorClient` — typed `HttpClient` implementing `IOrchestratorClient`
- `GET /api/correlations/{idSystemA}` → 200 returns `OrchestratorResult`; 404 returns `null` (triggers heuristic); other status throws
- `X-Api-Key` header injected at registration time
- `AddStandardResilienceHandler()` via `Microsoft.Extensions.Http.Resilience` — exponential retry (3×), circuit breaker, per-attempt and total timeouts
- Registered only in non-Development; `StubOrchestratorClient` remains the Development default

#### Secrets via Environment Variables
- All sensitive values in `appsettings.Production.json` set to `""` — env vars take precedence at runtime
- `.env.example` documents every required production env var with naming convention (`ConnectionStrings__SqlServer`, `Dinamo__Password`, `Orchestrator__ApiKey`, etc.)
- `appsettings.json` (dev values) unchanged — only loaded in Development

---

## Phase 4 — Observability & Comparison Dashboard ✅ Complete

**Goal:** Make the coexistence layer fully observable for the architectural review period — discrepancies must be surfaced immediately and the heuristic fallback rate must be measurable.

### Deliverables

#### Discrepancy Storage
- `SpiDiscrepancy` entity (`src/Domain/Entities/SpiDiscrepancy.cs`) — fields: `Id`, `IdSystemA`, `IdSystemB`, `CorrelationSource`, `Field`, `SystemAValue`, `SystemBValue`, `DetectedAt`
- `ISpiDiscrepancyRepository` + `SpiDiscrepancyRepository` — `AddRangeAsync` batches rows in one `SaveChangesAsync`
- `SpiDiscrepancyConfiguration` — table `SpiDiscrepancies`, index on `(IdSystemA, DetectedAt)`
- EF migration `AddDiscrepancyTable` — creates table + index
- `SpiComparisonConsumer` updated to persist one row per mismatched field before publishing to Kafka

#### Metrics (RF-05, RF-08)
- `ISpiMetrics` interface + `SpiMetrics` implementation using `System.Diagnostics.Metrics.Meter("ConvivenciaPix")`
- Instruments:
  - `spi.correlation.source` counter — tags: `source` (Orchestrator/Heuristic)
  - `spi.proxy.response_latency_ms` histogram
  - `spi.discrepancies.total` counter — tags: `field`
  - `spi.dlq.messages` counter — tags: `topic` (incremented in `KafkaConsumerBase` on DLQ routing)
- `SpiProxyApi`: Prometheus scraping endpoint on `/metrics` via `OpenTelemetry.Exporter.Prometheus.AspNetCore 1.10.0-beta.1`
- Workers: OTLP push via `OpenTelemetry.Exporter.OpenTelemetryProtocol`

#### Tracing Enrichment
- `SpiActivitySource` static class — `ActivitySource("ConvivenciaPix", "1.0.0")`
- `SystemAResponseProxyConsumer` — named spans: `proxy.correlate-lookup`, `proxy.xml-sign`, `proxy.redis-deposit`
- CorrelationId Kafka header propagated into `Activity.Current.SetBaggage("correlation-id", ...)`
- All entry points register `AddSource("ConvivenciaPix")` with OTel tracing builder

#### Alerting
- `infra/prometheus/alert_rules.yml` — three rules: `SpiDlqMessagesHigh`, `SpiHeuristicFallbackHigh`, `SpiDiscrepanciesDetected`

#### Local Infrastructure
- `infra/prometheus/prometheus.yml` — scrapes `spi-proxy-api:9090/metrics` at 15s interval
- `infra/grafana/provisioning/` — Prometheus datasource + dashboard provider
- `infra/grafana/dashboards/spi-overview.json` — 5 panels: Correlation Source (pie), Response Latency p50/p95/p99 (timeseries), Discrepancies by Field (bar), DLQ Rate (stat), Heuristic % (gauge with 20% threshold)
- `docker-compose.yml` — added Prometheus (port 9090) and Grafana (port 3000) services

---

## Phase 5 — Testing (Planned)

**Goal:** Achieve test coverage across domain, application, integration, and end-to-end scenarios to validate the coexistence layer before production cutover.

### Planned Deliverables

#### Domain Unit Tests (`tests/ConvivenciaPix.Domain.Tests`)
- `SpiSentMsg.Create` — validates required field guards, `CorrelationSource` variants
- `SpiPendingSystemBMsg.Create` — validates guards, Base64 round-trip via `DecodeRawXml()`
- `CorrelationSource.From` — valid values, invalid value throws `ArgumentException`

#### Application Unit Tests (`tests/ConvivenciaPix.Application.Tests`)
- `SpiController` — mocked `IResponseCache`, `IKafkaPublisher`, `ISpiXmlParser`; covers idempotency hit, normal flow with poll success, 30s timeout path, invalid XML rejection
- `SystemAResponseCorrelateConsumer` — mocked `IOrchestratorClient` and `ISpiPendingSystemBMsgRepository`; covers orchestrator success, heuristic match, no-match exception
- `SystemAResponseProxyConsumer` — covers retry loop (correlation found on attempt N), correlation not found after 5 retries, error response propagation

#### Infrastructure Integration Tests (`tests/ConvivenciaPix.Infrastructure.Tests`)
Using **Testcontainers** (packages already referenced in `Directory.Packages.props`):
- `SpiSentMsgRepository` — real SQL Server container; `AddAsync`, `FindByIdSystemAAsync`, `FindByIdSystemBAsync`, `DeleteOlderThanAsync`
- `SpiPendingSystemBMsgRepository` — `FindHeuristicMatchAsync` with boundary timestamp window cases (inside window, outside window, exact edge)
- `RedisResponseCache` — real Redis container; idempotency NX semantics (second write is ignored), TTL expiry
- `XmlSigningService` — `SignAsync` + `VerifyAsync` round-trip with a self-signed certificate

#### End-to-End Integration Tests (`tests/ConvivenciaPix.Integration.Tests`)
Full pipeline test with real SQL Server + Kafka + Redis containers:
1. POST a valid SPI XML to `SpiProxyApi` → confirm HTTP 504 when no response is deposited (timeout path)
2. POST SPI XML → manually write to `spi.systema.responses` via Kafka producer → confirm correlate worker saves `SpiSentMsg` and proxy worker deposits in Redis → confirm API returns signed XML
3. POST same `MessageId` twice → confirm idempotent response on second call (no Kafka publish)
4. POST SPI XML with mismatched amounts in System A vs System B XML → confirm `spi.discrepancies` receives `SpiDiscrepancyDetected`

---

## Functional Requirements Coverage

| ID | Requirement | Phase |
|:---|:---|:---|
| RF-01 | Bacen SPI endpoint emulation with mTLS and XML signatures | Phase 2 (signing ✅), Phase 3 (mTLS) |
| RF-02 | Hybrid correlation (Orchestrator + Heuristic fallback) | Phase 2 ✅ |
| RF-03 | Idempotency via `MessageId` | Phase 2 ✅ |
| RF-04 | Error propagation from System A to System B | Phase 2 ✅ |
| RF-05 | `CorrelationSource` logging and storage | Phase 2 ✅ |
| RF-06 | Database indexing on both system IDs | Phase 2 ✅ |
| RF-07 | Dead Letter Queues for all Kafka consumers | Phase 1 ✅ (base class) |
| RF-08 | Comparison reporting with discrepancy logging | Phase 2 ✅ (Kafka), Phase 4 (DB table + dashboard) |
| RF-09 | Response caching + configurable request timeout | Phase 2 ✅ |

---

## Architecture Summary

```
System B ──► POST /api/spi/messages ──► spi.systemb.requests (Kafka)
                     │                         │
                     │ poll Redis               ▼
                     │              SystemBSentConsumer
                     │              (spi-correlate-systemb)
                     │                 stores SpiPendingSystemBMsg
                     │
System A ──► Bacen ──► SpiOutbox ──► Debezium ──► spi.systema.responses (Kafka)
                                                          │
                                          ┌───────────────┼───────────────┐
                                          ▼               ▼               ▼
                             SystemAResponse         SystemAResponse  SpiComparison
                             CorrelateConsumer       ProxyConsumer    Consumer
                             (correlate-systema)     (proxy-systema)  (comparison)
                                    │                     │               │
                             Orchestrator/           Sign XML        Compare fields
                             Heuristic match         Redis deposit   (A vs B)
                                    │                     │               │
                             SpiSentMsg (DB)        response:{B}   SpiDiscrepancy
                                                    (unblocks API)  Detected
                                                          │
                                          System B ◄── GET Redis
```

---

## Local Development Quick Start

```bash
# 1. Start infrastructure
docker compose up -d

# 2. Apply EF migrations (requires SQL Server healthy)
dotnet tool restore
dotnet tool run dotnet-ef database update --project src/Infrastructure --startup-project src/SpiProxyApi

# 3. Register Debezium connector
curl -X POST http://localhost:8083/connectors \
  -H "Content-Type: application/json" \
  -d @infra/debezium/connectors/systema-outbox.json

# 4. Run services (separate terminals)
dotnet run --project src/SpiProxyApi
dotnet run --project src/SpiCorrelateWorker
dotnet run --project src/SpiProxyWorker
dotnet run --project src/SpiComparisonEngine

# 5. Send a test SPI message
curl -X POST http://localhost:5000/api/spi/messages \
  -H "Content-Type: application/xml" \
  -d '<Document>...</Document>'

# 6. Inspect topics
open http://localhost:8080   # Kafka UI
open http://localhost:16686  # Jaeger tracing
open http://localhost:8084   # Debezium UI
```
