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
| RF-10 | Performance: Sub-millisecond signaling | Phase 5 🏗️ |

---

## Architecture Summary (Target)

```
System B ──► POST /api/spi/messages ──► ReceiveSpiRequestUseCase
                     │                         │
                     │ wait (Redis Pub/Sub)    ▼
                     │              spi.systemb.requests (Kafka)
                     │                         │
                     │              CorrelateMessagesUseCase
                     │              (Hybrid strategy)
                     │                         │
System A ──► Bacen ──► SpiOutbox ──► Debezium ──► spi.systema.responses (Kafka)
                                                          │
                                          ┌───────────────┴───────────────┐
                                          ▼                               ▼
                             PropagateResponseUseCase            SpiComparisonEngine
                             (Sign XML + Redis Publish)          (Detect Discrepancy)
                                          │                               │
                                    response:{B}                    SpiDiscrepancy (DB)
                                    (Unblocks API)
```
