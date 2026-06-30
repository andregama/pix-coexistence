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
| RF-02 | Hybrid correlation (Orchestrator + Heuristic fallback) | Phase 2 ✅ |
| RF-03 | Idempotency via `MessageId` | Phase 2 ✅ |
| RF-04 | Error propagation from System A to System B | Phase 2 ✅ |
| RF-05 | `CorrelationSource` logging and storage | Phase 2 ✅ |
| RF-06 | Database indexing on both system IDs | Phase 2 ✅ |
| RF-07 | Dead Letter Queues for all Kafka consumers | Phase 1 ✅ |
| RF-08 | Comparison reporting with discrepancy logging | Phase 4 ✅ |
| RF-09 | Response caching + configurable request timeout | Phase 2 ✅ |
| RF-10 | Performance: Sub-millisecond signaling | Phase 5 ✅ |
| RF-11 | Dual-flow correlation (outbound `SpiSentMsg` + inbound `SpiReceivedMsg`) | Phase 8 🏗️ |
| RF-12 | Message type filtering via `Correlation:AllowedMessageTypes` | Phase 8 🏗️ |

---

## Architecture Summary (Target — Phase 8+)

```
System B ──► POST /api/spi/messages ──► ReceiveSpiRequestUseCase
                     │                         │
                     │ wait (Redis Pub/Sub)    ▼
                     │              spi.systemb.requests (Kafka)
                     │                         │
                     │            ┌────────────┴────────────────────────┐
                     │            ▼ (outbound)                          ▼ (inbound)
                     │  CorrelateSystemAOutboundUseCase    CorrelateSystemAInboundUseCase
                     │  (EndToEndId → SpiSentMsg)          (EndToEndId → SpiReceivedMsg)
                     │            │                                      │
                     │            ▼                                      ▼
System A ──► Bacen ──► SpiEnvioApiBacen ──► Debezium ──► spi.systema.outbound (Kafka)
                    └──► SpiRecepApiBacen ──► Debezium ──► spi.systema.inbound (Kafka)
                                                                         │
                                                         ┌───────────────┴───────────────┐
                                                         ▼                               ▼
                                            PropagateResponseUseCase            SpiComparisonEngine
                                            (Sign XML + Redis Publish)          (Detect Discrepancy)
                                                         │                               │
                                                   response:{B}                    SpiDiscrepancy (DB)
                                                   (Unblocks API)
```

## Phase 8 — Schema Restructure & Dual-Flow Correlation 🏗️ Pending

**Goal:** Restructure the correlation database schema and update the workers to support both Sent (outbound) and Received (inbound) messages, filter by message types, and optimize the correlation flow using direct idempotent identifiers mapped from System A's distinct tables (`SpiRecepApiBacen` and `SpiEnvioApiBacen`).

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
Stores all incoming messages/responses received from Bacen. Debezium streams these to **`spi.systema.inbound`**.
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
Stores all outgoing messages sent by System A to Bacen. Debezium streams these to **`spi.systema.outbound`**.
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
Used for transactions initiated by the bank (PSP). The external Orchestrator pre-inserts a record into this table before either system generates their respective XML.
* Schema:
  * **`IdempotentId`** `VARCHAR(255)` (Primary Key): Stores the `EndToEndId` of the transfer (e.g. for `pacs.008`, `pacs.004`, and `pacs.002`).
  * **`MsgIdSystemA`** `VARCHAR(255)` (Nullable, Indexed): Mapped from `MessageId` in `SpiEnvioApiBacen`.
  * **`MsgIdSystemB`** `VARCHAR(255)` (Nullable, Indexed): Mapped from System B's outbound request.
  * **`XmlMsgSystemA`** `NVARCHAR(MAX)` (Nullable): Mapped from `XmlMsg` in `SpiEnvioApiBacen`.
  * **`XmlMsgSystemB`** `NVARCHAR(MAX)` (Nullable): Mapped from System B's request XML.
  * **`OriginalMsgIdempotentId`** `VARCHAR(255)` (Nullable, Indexed): Mapped from return transactions (`pacs.004`) pointing back to the original transfer's `EndToEndId`.
  * **`SystemAErrorCode`** `VARCHAR(MAX)` (Nullable): Mapped from `Problem` in `SpiEnvioApiBacen`.
  * **`SystemBErrorCode`** `VARCHAR(MAX)` (Nullable): Mapped from System B's error/failures.
  * **`CreatedAt`** `DATETIME2` (Not Null): Pre-populated when the Orchestrator initiates the record.
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
  * **`CorrelationSource`** `VARCHAR(50)` (Not Null): e.g. `"DirectEndToEnd"`, `"Orchestrator"`, `"Heuristic"`.
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

`SpiCorrelateWorker` will process CDC streams from System A (`spi.systema.inbound` and `spi.systema.outbound`) and System B (`spi.systemb.requests`).

#### A. Outbound Flow (Sent Messages)
Uses System A's outbound CDC (`spi.systema.outbound`) mapped from `SpiEnvioApiBacen`.
1. **Consume System A outbound message:**
   - Parse CDC event from `SpiEnvioApiBacen`.
   - Extract `EndToEndId` (use as `IdempotentId`) from `XmlMsg`.
   - Verify that the message type is in `AllowedMessageTypes`.
   - Query `SpiSentMsg` by `IdempotentId`.
   - If found: update `MsgIdSystemA = MessageId`, `XmlMsgSystemA = XmlMsg`, `SystemAErrorCode = Problem`, and `UpdatedAt = UtcNow`.
   - If not found: log a warning and retry or route to DLQ (as the Orchestrator is expected to have pre-inserted the row).
2. **Consume System B message (`spi.systemb.requests`):**
   - Extract `EndToEndId` (use as `IdempotentId`), `MsgId`, `XmlMsg`, and `ErrorCode` from the Kafka envelope.
   - Verify that the message type is in `AllowedMessageTypes`.
   - Query `SpiSentMsg` by `IdempotentId`.
   - If found: update `MsgIdSystemB = MsgId`, `XmlMsgSystemB = XmlMsg`, `SystemBErrorCode = ErrorCode`, and `UpdatedAt = UtcNow`.
   - If not found: log a warning and retry or route to DLQ.
3. **Completion:**
   - When both `XmlMsgSystemA` and `XmlMsgSystemB` are populated, publish a correlation event to `spi.correlation.events`.

#### B. Inbound Flow (Received Messages)
Uses System A's inbound CDC (`spi.systema.inbound`) mapped from `SpiRecepApiBacen`.
1. **Consume System A inbound message:**
   - Parse CDC event from `SpiRecepApiBacen`.
   - Verify that the message type is allowed.
   - Extract `EndToEndId` (use as `IdempotentId`), `MsgId` (extracted from `<AppHdr>/<MsgId>` inside `XmlMsg`), `XmlMsg`, and `Problem`.
   - Query `SpiReceivedMsg` by `IdempotentId`.
   - If not exists: Insert a new row setting `IdempotentId`, `MsgId` (first-wins), `XmlMsgSystemA = XmlMsg`, `SystemAErrorCode = Problem`, `CorrelationSource = "DirectEndToEnd"`, and `CreatedAt = UtcNow`.
   - If exists: Update `XmlMsgSystemA = XmlMsg`, `SystemAErrorCode = Problem`, and `UpdatedAt = UtcNow` (do **not** overwrite `MsgId` if already set — first-wins).
2. **Consume System B inbound message:**
   - Verify that the message type is allowed.
   - Extract `EndToEndId` (use as `IdempotentId`), `MsgId`, `XmlMsg`, and `ErrorCode`.
   - Query `SpiReceivedMsg` by `IdempotentId`.
   - If not exists (System B processed it first): Insert a new row setting `IdempotentId`, `MsgId` (first-wins), `XmlMsgSystemB = XmlMsg`, `SystemBErrorCode = ErrorCode`, `CorrelationSource = "DirectEndToEnd"`, and `CreatedAt = UtcNow`.
   - If exists: Update `XmlMsgSystemB = XmlMsg`, `SystemBErrorCode = ErrorCode`, and `UpdatedAt = UtcNow` (do **not** overwrite `MsgId` if already set — first-wins).
3. **Completion:**
   - When both `XmlMsgSystemA` and `XmlMsgSystemB` are populated, publish the correlation event to `spi.correlation.events`.

#### C. Proxy Response Propagation Worker (`SpiProxyWorker`)
System B pulls signed inbound responses/status reports from the proxy stream.
* `SpiProxyWorker`'s consumer `SystemAResponseProxyConsumer` must be updated to consume from the **`spi.systema.inbound`** topic (which maps System A's received messages from `SpiRecepApiBacen`).
* It will sign the received System A XML using the HSM abstraction and enqueue it on the outbound stream so System B can pull it.

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
- Trigger the orchestrator mock to register a sent transfer, then publish mock messages for System A (to `spi.systema.inbound` and `spi.systema.outbound` respectively) and System B to Kafka, validating that the correlation worker matches them and updates the new DB schema columns correctly.


