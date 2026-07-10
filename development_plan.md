# Development Plan — Pix Coexistence Layer

## Overview

This document describes the full development plan for the **ConvivenciaPix** solution — a coexistence layer that allows System B (internal Pix implementation) to run in parallel with System A (proprietary vendor system) for homologation, while System A remains the sole connection point to Brazil's Central Bank (Bacen) SPI APIs.

The solution is built on **.NET 8**, **Clean Architecture / DDD**, **Kafka**, **SQL Server**, and **Redis**.

---

## Phase 1 — Infrastructure Scaffold ✅ Complete

**Goal:** Establish the architectural skeleton, domain model, and all infrastructure adapters.

### Deliverables
- **Domain Layer:** `SpiSentMsg`, `CorrelationSource`, `SpiDiscrepancyDetected`.
- **Application Layer:** Canonical DTOs, HSM and Cache abstractions, XML Signing port.
- **Infrastructure Layer:** EF Core persistence, Redis cache with idempotency namespaces, Abstract Kafka Consumers with DLQ routing.
- **Local Dev:** Full `docker-compose` stack (SQL Server, Kafka, Redis, Debezium, Jaeger).

---

## Phase 2 — Business Logic Implementation ✅ Complete (Refactoring Required)

**Goal:** Implement message-processing logic and core workers.

### Deliverables
- **API:** `SpiProxyApi` with full SPI endpoint emulation and polling mechanism.
- **Correlation:** `SpiCorrelateWorker` with Hybrid strategy (Orchestrator + Heuristic).
- **Proxying:** `SpiProxyWorker` for signing and response propagation.
- **Comparison:** `SpiComparisonEngine` for business logic validation.

*Note: Initial implementation has logic "leaked" into Controllers and Consumers. Refactoring to Use Cases is planned in Phase 5.*

---

## Phase 3 — Security Hardening ✅ Complete

**Goal:** Harden the solution with mTLS, HSM integration, and resilient external clients.

### Deliverables
- **mTLS:** `BacenCertificateValidator` and certificate authentication middleware.
- **HSM:** Two-layer abstraction for Dinamo HSM (Real vs. Mock).
- **Orchestrator:** Resilient `HttpOrchestratorClient` with exponential backoff and circuit breakers.

---

## Phase 4 — Observability & Comparison Dashboard ✅ Complete

**Goal:** Make the coexistence layer fully observable.

### Deliverables
- **Persistence:** Discrepancy storage in SQL Server.
- **Metrics:** `System.Diagnostics.Metrics` for latency, correlation accuracy, and DLQ rates.
- **Dashboard:** Grafana dashboard with real-time SPI overview and alerting rules.
- **Tracing:** OpenTelemetry enrichment across all workers.

---

## Phase 5 — Refactoring & Architectural Hardening ✅ Complete

**Goal:** Realign the codebase with Clean Architecture principles and optimize core performance.

### Deliverables
- **Use Cases Extraction:**
  - `ReceiveSpiRequestUseCase`: Consolidate API logic, idempotency, and signaling. ✅
  - `CorrelateMessagesUseCase`: Consolidate hybrid correlation logic. ✅
  - `ReceiveSystemBSentUseCase`: Consolidate pending record storage logic. ✅
  - `PropagateResponseUseCase`: Consolidate signing and delivery logic. ✅
- **Signaling Optimization:** Replaced 500ms polling in `SpiProxyApi` with **Redis Pub/Sub** via `IResponseCache.WaitForResponseAsync` for sub-millisecond overhead. ✅
- **Request Validation:** Implemented `FluentValidation` for incoming SPI XML structures via `SpiRequestValidator`. ✅

---

## Phase 6 — Comprehensive Testing ✅ Complete (Unit) / 🏗️ Pending (Integration)

**Goal:** Achieve 90%+ coverage across all layers and validate the full E2E pipeline.

### Deliverables
- **Domain Unit Tests:** Complete coverage for all entities and value objects. ✅
- **Application Unit Tests:** Test Use Cases in isolation using Mocks (Moq/NSubstitute). ✅
- **Infrastructure Integration Tests:** Validate Repository and Cache behavior using **Testcontainers**. (Implemented, requires Docker) 🏗️
- **End-to-End Integration Tests:** Full pipeline validation using Testcontainers (Kafka + SQL + Redis). (Implemented, requires Docker) 🏗️

---

## Phase 7 — Production Readiness & DX ✅ Complete

**Goal:** Final polish for production deployment and developer productivity.

### Deliverables
- **Resilience:** Implemented **Rate Limiting** in `SpiProxyApi` using a global Fixed Window strategy (100 req/10s). ✅
- **Security:** Added **CRL/OCSP** online revocation support to `BacenCertificateValidator`. ✅
- **Developer Automation (Makefile):** ✅
  - `make infra-up`: Start local stack.
  - `make infra-down`: Stop containers.
  - `make migrate`: Apply the SQL scripts in `infra/sql/` (idempotent, no EF migrations).
  - `make test`: Run full test suite.
  - `make lint`: Run `dotnet format`.
- **Documentation:** Integrated **Swagger / OpenAPI** for ISO 20022 payloads with full XML comment support. ✅

---

## Functional Requirements Coverage

| ID | Requirement | Phase |
|:---|:---|:---|
| RF-01 | Bacen SPI endpoint emulation with mTLS and XML signatures | Phase 3 ✅ |
| RF-02 | ~~Hybrid correlation (Orchestrator + Heuristic)~~ → Direct Bacen idempotency-key correlation | Phase 8 ✅ |
| RF-03 | Idempotency via `MessageId` | Phase 2 ✅ |
| RF-04 | Error propagation from System A to System B | Phase 2 ✅ |
| RF-05 | ~~`CorrelationSource` logging~~ (removed with Orchestrator/Heuristic in Phase 8) | — |
| RF-06 | Database indexing on both system IDs | Phase 2 ✅ |
| RF-07 | Dead Letter Queues for all Kafka consumers | Phase 1 ✅ |
| RF-08 | Comparison reporting with discrepancy logging | Phase 4 ✅ |
| RF-09 | Response caching + configurable request timeout | Phase 2 ✅ |
| RF-10 | Performance: pull-based outbound stream (Bacen SPI ICOM §2.2.2) | Phase 5 ✅ |
| RF-11 | Dual-flow correlation (outbound `SpiSentMsg` + inbound `SpiReceivedMsg`) | Phase 8 ✅ |
| RF-12 | Message type filtering via `Correlation:AllowedMessageTypes` | Phase 8 ✅ |
| RF-13 | Response transformation to System B's expected values (config-driven rules) | Phase 9 ✅ |
| RF-14 | Sent-side self-sufficiency: first-arrival creates `SpiSentMsg`, no external pre-insert | Phase 9 ✅ |
| RF-15 | Consume a single merged System A CDC topic, dispatching by `source.table` (production-parity) | Phase 10 ✅ |
| RF-16 | Message-type-aware idempotency-key extraction (`pibr.001` → `EchoReq/GrpHdr/MsgId`; no longer assumes `EndToEndId`) | Phase 11 ✅ |
| RF-17 | SPI Echo (`pibr.001`) answered with a synthesised, signed `pibr.002`, bypassing correlation | Phase 11 ✅ |
| RF-18 | XML signing through the HSM only (real Dinamo `SignPIX`/`VerifyPIX`); signature in `AppHdr/Sgntr` | Phase 12 ✅ |
| RF-19 | End-to-end coverage of every coexistence flow (Testcontainers full pipeline) | Phase 13 ✅ |

---

## Architecture Summary (Current — Phase 13)

```
System B ──► POST /api/v1/in/{ispb}/msgs ──► ReceiveSpiRequestUseCase ──► spi.systemb.requests
   ▲                                                                              │
   │ GET /api/v1/out/{ispb}/stream (long-poll pull + DELETE ack)                  │
   │                        spi-correlate-worker  (consumes the merged System A CDC topic +       │
   │                        the System B request topic; dispatches by source.table; correlates    ▼
   │                        by shared IdempotentId)
   │                          • Outbound (SpiEnvioApiBacen): assemble SpiSentMsg A/B pacs.008 pair —
   │                            1st arrival CREATES the row, 2nd COMPLETES it ─► spi.correlation.events
   │                          • Inbound (SpiRecepApiBacen): record SpiReceivedMsg, look up the pair,
   │                            TRANSFORM the System A response → System B's expected values, publish ─┐
   │                                                                                                   │
System A ─► Bacen ─► SpiEnvioApiBacen ─┐                                                              │
                  └─► SpiRecepApiBacen ─┴─► Debezium ─► spi.systema.cdc ──► (source.table dispatch)   ▼
                                                                              │      spi.systemb.responses
                        ┌─────────────────────────────────────────────────────┤              │
                        ▼                                                       ▼              ▼
              spi-comparison-engine                                   spi-comparison-   spi-proxy-worker
              (consume comparison events →                            engine            consume responses →
               detect field diffs → SpiDiscrepancy DB)                                  sign transformed XML →
                                                                                        enqueue on Redis
                                                                                        outbound stream
```

> The proxy worker no longer consumes System A CDC directly. The correlate worker owns the merged
> `spi.systema.cdc` topic, performs the A→B field transformation, and hands the proxy worker a ready-to-sign payload on
> `spi.systemb.responses`.
>
> **SPI Echo (`pibr.001`)** takes a shortcut through the same machinery: the correlate worker dispatches it by
> message type *before* the correlation gate, synthesises the `pibr.002` EchoRpt, and publishes it straight to
> `spi.systemb.responses`. The proxy worker signs and enqueues it like any other response — no correlation,
> no System A leg. Signing goes through `IHsmService` only (Dinamo `SignPIX`), placing the signature in `AppHdr/Sgntr`.

## Phase 8 — Schema Restructure & Dual-Flow Correlation ✅ Complete

**Goal:** Restructure the correlation database schema and update the workers to support both Sent (outbound) and Received (inbound) messages, filter by message types, and optimize the correlation flow using direct idempotent identifiers mapped from System A's distinct tables (`SpiRecepApiBacen` and `SpiEnvioApiBacen`).

*Delivered in commit `cdc9195`: the Orchestrator + Heuristic correlation (and `IOrchestratorClient`, `CorrelationSource`, `SpiPendingSystemBMsg`) was removed in favour of direct Bacen idempotency-key lookup; `SpiSentMsg` and `SpiReceivedMsg` were split; `spi.systema.outbound` / `spi.systema.inbound` topics replaced `spi.systema.responses`.*

### Deliverables

**Database:**
- `004_SpiRestructureSchema.sql`: Drop/recreate `SpiSentMsg` with new schema; create `SpiReceivedMsg`. 🏗️

**Domain Layer:**
- Restructure `SpiSentMsg` entity: replace `IdSystemA`/`IdSystemB`/`CorrelationSource` with `IdempotentId`, `MsgIdSystemA`, `MsgIdSystemB`, `XmlMsgSystemA`, `XmlMsgSystemB`, `OriginalMsgIdempotentId`, `SystemAErrorCode`, `SystemBErrorCode`, `UpdatedAt`. 🏗️
- Create `SpiReceivedMsg` entity (replaces `SpiPendingSystemBMsg`). 🏗️
- Extend `CorrelationSource` value object with `DirectEndToEnd` value. 🏗️
- Add `ISpiReceivedMsgRepository` interface. 🏗️
- Update `ISpiSentMsgRepository`: replace `FindByIdSystemAAsync`/`FindByIdSystemBAsync` with `FindByIdempotentIdAsync`, add `UpdateAsync`. 🏗️

**Application Layer:**
- Add `string ExtractMessageType(string xml)` to `ISpiXmlParser`. 🏗️
- Split `SystemAOutboxMapper` into `SystemAInboundMapper` (maps `SpiRecepApiBacen` CDC) and `SystemAOutboundMapper` (maps `SpiEnvioApiBacen` CDC). 🏗️
- Replace `CorrelateMessagesUseCase` (Orchestrator+Heuristic) with two new use cases: `CorrelateSystemAOutboundUseCase` and `CorrelateSystemAInboundUseCase`, both keyed on `EndToEndId`. 🏗️
- Add `Correlation:AllowedMessageTypes` config binding; apply filtering in both use cases. 🏗️
- Remove `ReceiveSystemBSentUseCase` / `SpiPendingSystemBMsg` path (superseded by direct lookup). 🏗️

**Infrastructure Layer:**
- Implement `ExtractMessageType` in `SpiXmlParser`. 🏗️
- Add `SpiReceivedMsgRepository` and EF `SpiReceivedMsgConfiguration`. 🏗️
- Update `SpiSentMsgRepository` and `SpiSentMsgConfiguration` to reflect new schema. 🏗️
- Update `Topics.cs`: replace `SystemAResponses` with `SystemAInbound` (`spi.systema.inbound`) and `SystemAOutbound` (`spi.systema.outbound`). 🏗️
- Update `SpiProxyWorker`'s consumer to subscribe to `spi.systema.inbound`. 🏗️
- Update `SpiSentMsgCleanupJob` if it references old schema columns. 🏗️

**Tests:**
- Update `SpiSentMsgTests` for new entity shape; add `SpiReceivedMsgTests`. 🏗️
- Replace `CorrelateMessagesUseCaseTests` with tests for both new use cases; remove `ReceiveSystemBSentUseCaseTests`. 🏗️
- Split `SystemAOutboxMapperTests` into inbound and outbound variants. 🏗️
- Add `SpiReceivedMsgRepositoryTests` (Testcontainers); update `SpiSentMsgRepositoryTests`. 🏗️
- Update `SpiXmlParserTests` to cover `ExtractMessageType`. 🏗️
- Update integration tests in `SpiPipelineTests` to exercise both inbound and outbound paths. 🏗️

---

### 1. System A Schema & Debezium Configuration

System A uses two separate tables for message processing, which Debezium streams as distinct CDC events to separate Kafka topics:

#### Table A.1: `SpiRecepApiBacen` (Inbound - SPI to PSP)
Stores all incoming messages/responses received from Bacen. Debezium streams these to the merged **`spi.systema.cdc`** topic (identified downstream by `source.table = "SpiRecepApiBacen"`).
* Schema:
  * `DtHrRecepcao` `datetime2` (Not Null)
  * `Ispb` `char(8)` (Not Null)
  * `PIResourceId` `varchar(50)` (Not Null)
  * `DtRecepcao` `date` (Not Null)
  * `ReturnCode` `int` (Not Null)
  * `ContentType` `varchar(100)` (Null)
  * `[Next]` `varchar(400)` (Null)
  * `Problem` `varchar(MAX)` (Null) — Stores System A's processing error/failures.
  * `XmlMsg` `varchar(MAX)` (Not Null) — Holds the raw XML message.
  * `DtHrProcessamento` `datetime2` (Null)

#### Table A.2: `SpiEnvioApiBacen` (Outbound - PSP to SPI)
Stores all outgoing messages sent by System A to Bacen. Debezium streams these to the merged **`spi.systema.cdc`** topic (identified downstream by `source.table = "SpiEnvioApiBacen"`).
* Schema:
  * `DtHrEnvio` `datetime2` (Not Null)
  * `MessageId` `varchar(50)` (Not Null) — The `<MsgId>` field from the XML.
  * `DtEnvio` `date` (Not Null)
  * `Ispb` `char(8)` (Not Null)
  * `PIResourceId` `varchar(50)` (Not Null)
  * `XmlMsg` `varchar(MAX)` (Not Null) — Holds the raw XML message.
  * `ContentType` `varchar(100)` (Null)
  * `Problem` `varchar(MAX)` (Null) — Stores System A's processing error/failures.

---

### 2. Coexistence Database Schema Restructure

Based on the distinct lifecycles of inbound and outbound transactions, the coexistence layer (`DB_COEXISTENCE`) will use two separate tables:

#### Table B.1: `SpiSentMsg` (Outbound/Sent - PSP to SPI)
Used for transactions initiated by the bank (PSP). The row is created by whichever of System A / System B outbound arrives first and completed by the other (first-arrival-creates — see Phase 9). *(The original design assumed an external Orchestrator pre-insert; that component was never implemented and is no longer required.)*
* Schema:
  * **`IdempotentId`** `VARCHAR(255)` (Primary Key): Stores the `EndToEndId` of the transfer (e.g. for `pacs.008`, `pacs.004`, and `pacs.002`).
  * **`MsgIdSystemA`** `VARCHAR(255)` (Nullable, Indexed): Mapped from `MessageId` in `SpiEnvioApiBacen`.
  * **`MsgIdSystemB`** `VARCHAR(255)` (Nullable, Indexed): Mapped from System B's outbound request.
  * **`XmlMsgSystemA`** `NVARCHAR(MAX)` (Nullable): Mapped from `XmlMsg` in `SpiEnvioApiBacen`.
  * **`XmlMsgSystemB`** `NVARCHAR(MAX)` (Nullable): Mapped from System B's request XML.
  * **`OriginalMsgIdempotentId`** `VARCHAR(255)` (Nullable, Indexed): Mapped from return transactions (`pacs.004`) pointing back to the original transfer's `EndToEndId`.
  * **`SystemAErrorCode`** `VARCHAR(MAX)` (Nullable): Mapped from `Problem` in `SpiEnvioApiBacen`.
  * **`SystemBErrorCode`** `VARCHAR(MAX)` (Nullable): Mapped from System B's error/failures.
  * **`CreatedAt`** `DATETIME2` (Not Null): Set when the first side (System A or System B outbound) creates the row.
  * **`UpdatedAt`** `DATETIME2` (Nullable): Set on updates.

#### Table B.2: `SpiReceivedMsg` (Inbound/Received - SPI to PSP)
Used for incoming transactions initiated by Bacen. Rows are dynamically created by the correlation worker when the first system's message arrives via CDC/Kafka.

> **Design decision:** A single `MsgId` field is used (not separate `MsgIdSystemA`/`MsgIdSystemB`) because inbound messages originate from Bacen — both System A and System B receive the same source message, so the `<MsgId>` is identical across both. Set on first insert; not overwritten on update.

* Schema:
  * **`IdempotentId`** `VARCHAR(255)` (Primary Key): Stores the `EndToEndId` extracted from `XmlMsg`.
  * **`MsgId`** `VARCHAR(255)` (Nullable, Indexed): The shared `<MsgId>` from the Bacen-originated message. Set on first insert (first-wins); not updated if already populated.
  * **`XmlMsgSystemA`** `NVARCHAR(MAX)` (Nullable): Mapped from `XmlMsg` in `SpiRecepApiBacen`.
  * **`XmlMsgSystemB`** `NVARCHAR(MAX)` (Nullable): Mapped from System B's XML payload.
  * **`OriginalMsgIdempotentId`** `VARCHAR(255)` (Nullable, Indexed)
  * **`SystemAErrorCode`** `VARCHAR(MAX)` (Nullable): Mapped from `Problem` in `SpiRecepApiBacen`.
  * **`SystemBErrorCode`** `VARCHAR(MAX)` (Nullable)
  * **`CreatedAt`** `DATETIME2` (Not Null): Set when the first message is inserted.
  * **`UpdatedAt`** `DATETIME2` (Nullable): Set on matches.

*Note: Create a new migration file `004_SpiRestructureSchema.sql` to execute these DDL changes.*

---

### 3. Message Type Identification & Filtering

`SpiCorrelateWorker` must identify and filter messages streamed from System A and System B tables.
1. **XML Parsing Update:**
   - Update `ISpiXmlParser` and `SpiXmlParser` with `string ExtractMessageType(string xml)` to extract the message type (e.g., `pacs.008`, `pacs.004`, `pacs.002`).
   - The type can be identified via the root element name (e.g., `<FIToFICstmrCdtTrf>` = `pacs.008`, `<PmtRtr>` = `pacs.004`, `<FIToFIPmtStsRpt>` = `pacs.002`) or the `<MsgDefIdr>` field inside the XML's `<AppHdr>`.
2. **Environment Variable Configuration:**
   - Add a configuration variable `Correlation:AllowedMessageTypes` (e.g., set to `"pacs.002,pacs.004,pacs.008"` in `appsettings.json` and `.env.example`).
3. **Filtering Behavior:**
   - The worker must parse the message XML and identify the type.
   - If the type is not present in the allowed types list, the worker must gracefully ignore/skip the message.

---

### 4. Restructured Correlation & Proxy Workers Flow

`SpiCorrelateWorker` processes the merged System A CDC stream (`spi.systema.cdc`, both tables — dispatched by `source.table`) and System B (`spi.systemb.requests`).

#### A. Outbound Flow (Sent Messages)
Uses System A's outbound CDC (`spi.systema.cdc`, `source.table = "SpiEnvioApiBacen"`).
1. **Consume System A outbound message:**
   - Parse CDC event from `SpiEnvioApiBacen`.
   - Extract `EndToEndId` (use as `IdempotentId`) from `XmlMsg`.
   - Verify that the message type is in `AllowedMessageTypes`.
   - Query `SpiSentMsg` by `IdempotentId`.
   - If found: update `MsgIdSystemA = MessageId`, `XmlMsgSystemA = XmlMsg`, `SystemAErrorCode = Problem`, and `UpdatedAt = UtcNow`.
   - If not found: **create** the row (`SpiSentMsg.Create`) and set the System A side. First-arrival-creates: the System B side completes it when it arrives. (No external Orchestrator pre-insert is required — see Phase 9.)
2. **Consume System B message (`spi.systemb.requests`):**
   - Extract `EndToEndId` (use as `IdempotentId`), `MsgId`, `XmlMsg`, and `ErrorCode` from the Kafka envelope.
   - Verify that the message type is in `AllowedMessageTypes`.
   - Query `SpiSentMsg` by `IdempotentId`.
   - If found: update `MsgIdSystemB = MsgId`, `XmlMsgSystemB = XmlMsg`, `SystemBErrorCode = ErrorCode`, and `UpdatedAt = UtcNow`.
   - If not found: **create** the row and set the System B side (first-arrival-creates; the System A side completes it later).
3. **Completion:**
   - When both `XmlMsgSystemA` and `XmlMsgSystemB` are populated, publish a correlation event to `spi.correlation.events` and a comparison event to `spi.comparison.events`.

#### B. Inbound Flow (Received Messages)
Uses System A's inbound CDC (`spi.systema.cdc`, `source.table = "SpiRecepApiBacen"`).
1. **Consume System A inbound message:**
   - Parse CDC event from `SpiRecepApiBacen`.
   - Verify that the message type is allowed.
   - Extract `EndToEndId` (use as `IdempotentId`), `MsgId` (extracted from `<AppHdr>/<MsgId>` inside `XmlMsg`), `XmlMsg`, and `Problem`.
   - Query `SpiReceivedMsg` by `IdempotentId`.
   - If not exists: Insert a new row setting `IdempotentId`, `MsgId` (first-wins), `XmlMsgSystemA = XmlMsg`, `SystemAErrorCode = Problem`, and `CreatedAt = UtcNow`.
   - If exists: Update `XmlMsgSystemA = XmlMsg`, `SystemAErrorCode = Problem`, and `UpdatedAt = UtcNow` (do **not** overwrite `MsgId` if already set — first-wins).
   - Look up the correlated `SpiSentMsg` pacs.008 pair, **transform** the response to System B's expected values (see Phase 9), and publish a ready-for-System-B event to `spi.systemb.responses`. If the pair is missing/incomplete, route to the DLQ.
2. **System B side (written by `SpiProxyWorker`, not a separate CDC consumer):**
   - After the proxy worker signs the transformed response and enqueues it for System B, it records the delivered payload on the same `SpiReceivedMsg` row: `XmlMsgSystemB = signed XML`, `UpdatedAt = UtcNow` (creating the row defensively only if the System A side has not landed yet).
3. **Completion:**
   - When both `XmlMsgSystemA` and `XmlMsgSystemB` are populated, publish the correlation event to `spi.correlation.events` and the comparison event to `spi.comparison.events`.

#### C. Proxy Response Propagation Worker (`SpiProxyWorker`)
System B pulls signed inbound responses/status reports from the proxy stream.
* `SpiProxyWorker`'s consumer `SystemBResponseProxyConsumer` consumes the **`spi.systemb.responses`** topic — the ready-for-System-B event published by the correlate worker after it transforms System A's response (see Phase 9). It does **not** read the raw `spi.systema.cdc` CDC topic; the correlate worker is that topic's sole consumer.
* It signs the already-transformed XML using the HSM abstraction, enqueues it on the Redis-backed outbound stream for System B to pull, records the delivered XML on `SpiReceivedMsg` (System B side), and — once both sides are present — emits the correlation/comparison events.

---

### 5. Verification Plan

#### Automated Tests
- Update unit tests in `tests/ConvivenciaPix.Domain.Tests` to cover the new entities (`SpiSentMsg` and `SpiReceivedMsg`).
- Update use case tests in `tests/ConvivenciaPix.Application.Tests` to mock the direct `EndToEndId` correlation lookup.
- Update `SystemAOutboxMapperTests` (or split into `SystemAInboundMapperTests` and `SystemAOutboundMapperTests`) to validate parsing CDC payloads for `SpiRecepApiBacen` and `SpiEnvioApiBacen`.
- Write new integration tests in `tests/ConvivenciaPix.Integration.Tests` using Testcontainers to verify both outbound and inbound tables schema mapping.
- Run `make test` to ensure all tests pass.

#### Manual Verification
- Deploy the updated database scripts and spin up the docker-compose stack.
- POST System B's pacs.008 to `/api/v1/in/{ispb}/msgs` and publish a matching System A outbound CDC event to `spi.systema.cdc` (`source.table = "SpiEnvioApiBacen"`, same `EndToEndId`), then confirm one `SpiSentMsg` row reaches completion (both `XmlMsg*` columns populated). Publish the System A inbound pacs.002 CDC to `spi.systema.cdc` (`source.table = "SpiRecepApiBacen"`) and confirm the transformed, signed response is delivered on the outbound stream.

---

## Phase 9 — Response Transformation & Correlation Self-Sufficiency ✅ Complete

**Goal:** Stop the proxy worker from consuming raw CDC, transform Bacen responses to System B's expected values, and make the sent-side correlation self-sufficient (no external Orchestrator).

### Deliverables

**Decoupling & transformation:**
- `SpiProxyWorker` no longer consumes System A CDC. The correlate worker is the sole consumer of the System A CDC topic; it correlates + transforms and publishes a ready-for-System-B event to **`spi.systemb.responses`**, which `SpiProxyWorker` consumes. ✅
- `IInboundResponseTransformer` / `InboundResponseTransformer` — rewrites System-A-specific fields in the response to System B's values, computed from the stored `SpiSentMsg` A/B pacs.008 pair. Rules are config-driven (`ResponseTransformOptions`, `ResponseTransform:Rules`), seeded in code with **EndToEndId** and the **initiation form** (`LclInstrm/Prtry`, e.g. `DICT`→`MANU`); rules whose target node is absent in the response are skipped. ✅
- `SystemBInboundReadyDto` carries the transformed XML on `spi.systemb.responses`. ✅
- `CorrelateSystemAInboundUseCase` now correlates + transforms + publishes; if the pacs.008 pair is missing or incomplete it routes to the DLQ (rather than delivering an untransformed response). ✅
- `PropagateResponseUseCase` slimmed to sign the transformed XML, enqueue it, record `SpiReceivedMsg` (System B side), and emit correlation/comparison events. The comparison event's System B XML is now the transformed+signed payload — a meaningful A-vs-B diff. ✅
- Renamed the proxy consumer `SystemAResponseProxyConsumer` → `SystemBResponseProxyConsumer` (new topic + consumer group). ✅

**Sent-side self-sufficiency:**
- `CorrelateSystemAOutboundUseCase` and `CorrelateSystemBOutboundUseCase` now **create-if-missing** on first arrival (reusing `SpiSentMsg.Create` + `UpdateFromSystemA/B`) instead of throwing when the row is absent, closing the gap left by the removed Orchestrator pre-insert. Mirrors the `SpiReceivedMsg` first-arrival-creates pattern. ✅

**Tests:**
- `InboundResponseTransformerTests`; `CorrelateSystemAInboundUseCaseTests`, `CorrelateSystemAOutboundUseCaseTests`, `CorrelateSystemBOutboundUseCaseTests`; updated `PropagateResponseUseCaseTests` and the integration round-trip (asserts the delivered response carries System B's `EndToEndId`). ✅

*Delivered in commit `1ffdf0c`.*

---

## Phase 10 — Single Merged System A CDC Topic ✅ Complete

**Goal:** Consume System A's CDC from a **single** Kafka topic (`spi.systema.cdc`) that carries both the `SpiRecepApiBacen` (inbound) and `SpiEnvioApiBacen` (outbound) tables, matching how Debezium is configured in production — replacing the previous two-topic / two-connector / two-consumer setup.

### Deliverables

- **Debezium:** replaced `systema-inbound.json` + `systema-outbound.json` with a single `systema-cdc.json` connector (`table.include.list` = both tables; `ByLogicalTableRouter` → `spi.systema.cdc`; full change envelope retained so `source` is available). ✅
- **Topics:** `Topics.SystemACdc = "spi.systema.cdc"` (+ `.dlq`); removed the obsolete `SystemAInbound`/`SystemAOutbound` constants. Consumer topic is overridable via `Kafka:SystemACdcTopic`. ✅
- **Dispatch:** new `CdcSource.ExtractTable` helper reads `source.table`; new `SystemACdcCorrelateConsumer` (single consumer, group `spi-correlate-systema-cdc`) routes each event to `CorrelateSystemAOutboundUseCase` (`SpiEnvioApiBacen`) or `CorrelateSystemAInboundUseCase` (`SpiRecepApiBacen`); unknown table / tombstone is logged and skipped. The two old dedicated consumers were removed. The use cases and mappers are unchanged. ✅
- **Infra/tests:** docker-compose provisions `spi.systema.cdc` (+ `.dlq`); the integration fixture registers the single consumer, and the round-trip publishes a full envelope with `source.table`. New `CdcSourceTests`. ✅

---

## Phase 11 — SPI Echo (`pibr.001` → `pibr.002`) Self-Service Flow ✅ Complete

**Goal:** Accept the SPI Echo keepalive (`pibr.001`) that System B originates itself and answer it with a synthesised, signed `pibr.002` (EchoRpt). Because there is no System A counterpart, the message must **not** go through correlation.

*Prerequisite (commit `5d1174a`): `ReceiveSpiRequestUseCase` no longer assumes every message carries an `EndToEndId`. It now resolves the message type first and extracts the message-type-aware **IdempotentId** via `SpiXmlParser.ExtractIdempotentId`, so `pibr.001` (and any future type) is enqueued with the correct key instead of being rejected.*

### Deliverables

- **Parser:** `SpiXmlParser` recognises `pibr.001`/`pibr.002` (via `MsgDefIdr` and the `EchoReq`/`EchoRpt` business element under the `<Envelope>` root) and extracts the Echo idempotency key from `EchoReq/GrpHdr/MsgId`. ✅
- **Builder:** new `IPibr002Builder` / `Pibr002Builder` synthesises the `pibr.002` — swaps `Fr`/`To`, mints a fresh schema-valid `MsgId`, sets `MsgDefIdr`/timestamps, and echoes `Data`→`OrgnlData`. ✅
- **Use case + dispatch:** new `GeneratePibr002UseCase` publishes a `SystemBInboundReadyDto(MsgType="pibr.002")` to `spi.systemb.responses`; `SystemBOutboundCorrelateConsumer` dispatches `pibr.001` to it (mirroring the CDC dispatch-by-table pattern) **before** the correlation gate, so correlation semantics and `AllowedMessageTypes` are untouched. Everything downstream (sign + enqueue + pull) is reused unchanged. A System-B-only `SpiReceivedMsg` row is written (`IsComplete=false`, so no comparison event fires). ✅
- **Schema source:** Catálogo de Serviços do SFN v5.12.1 (`pibr.001`/`pibr.002` XSDs + examples). ✅

## Phase 12 — HSM-Only PIX Signing via the Real Dinamo SDK ✅ Complete

**Goal:** Sign SPI XML through the HSM **only**. The Dinamo HSM signs the whole PIX envelope internally (`SignPIX`, signature in `AppHdr/Sgntr`), making the managed BCL signer redundant — and it had been placing the signature at the document root instead.

### Deliverables

- **Interfaces:** `IHsmService` is now the single XML-signing surface (`SignXmlAsync` / `VerifyXmlAsync`); `IXmlSigningService`/`XmlSigningService` were deleted. `IDinamoSdkClient` reshaped to the SDK's `SignPIX`/`VerifyPIX` (+ `Connect`/`Disconnect`). `PropagateResponseUseCase` calls `IHsmService.SignXmlAsync` directly. ✅
- **Signer:** the enveloped-signature logic moved into an internal `EnvelopedXmlSigner` that inserts the signature into `AppHdr/Sgntr` (root fallback), shared by `MockHsmService` (Dev) and `LocalDinamoSdkClient` (Staging). ✅
- **Real SDK:** new `DinamoNetSdkClient` wraps `Dinamo.Hsm.DinamoClient`; DI registers it in Production, `LocalDinamoSdkClient` in Staging, `MockHsmService` in Development. Added `Dinamo.Hsm` 4.26.0 (public nuget, net8.0). `DinamoOptions` → `CertId`/`KeyId`/`ChainId`/`Crl` (dropped `SignMechanism`); all `Dinamo` appsettings updated. ✅
- **Tests:** `EnvelopedXmlSigner` (asserts `AppHdr/Sgntr` placement, round-trip, tamper), `DinamoHsmService` (call order + disconnect-on-throw), `LocalDinamoSdkClient` (SignPIX↔VerifyPIX), and `DinamoNetSdkClient` (managed guards + a reflection contract guard against the real SDK signatures). ✅

## Phase 13 — Comprehensive End-to-End Test Coverage ✅ Complete

**Goal:** Prove every coexistence flow end-to-end against real infrastructure.

### Deliverables

- **Harness:** the Testcontainers integration fixture now hosts **all four** worker consumers in-process (added `SpiComparisonConsumer`), and a shared collection fixture (`IntegrationTestCollection`) reuses one container set across test classes. ✅
- **Flows (`SpiFlowTests`):** `pibr.001`→signed `pibr.002` (signature in `AppHdr/Sgntr`); ingest edge cases (415, duplicate); System B outbound persists `SpiSentMsg`; System A+B outbound complete the shared row; divergent business fields raise a discrepancy (persisted **and** published, RF-08); inbound Bacen error propagated to System B as a signed `RJCT` (RF-04); uncorrelated inbound routed to the DLQ (RF-07); delete-unknown-stream → 410. ✅
- **Reliability:** helpers drain-until-found on stream pulls, consume Kafka with fresh groups from earliest, poll the DB for RF-08 persistence, and sequence the two correlation sides to avoid the create-if-missing race. Full suite: **14/14 green** (Docker required). ✅


