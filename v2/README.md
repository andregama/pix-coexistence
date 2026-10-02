# Pix Coexistence v2 — SPI forwarding proxy

Second version of the coexistence solution. It is independent from the first one at the repository root: own solution, tables, Redis channels, Kafka topics, docker-compose and tests. Nothing here references code outside `v2/`.

## What it does

`SpiProxyApi` is the SPI endpoint for **both** System A and System B.

- **Sending.** A system posts messages exactly as it would to Bacen. The proxy records who sent each message and forwards the request body untouched to the real SPI, returning Bacen's status, `PI-ResourceId` and body as they came (so Bacen errors reach the sender unchanged).
- **Receiving.** `SpiInboundWorker` is the only consumer of the bank's SPI output stream. For each message it verifies Bacen's signature, decides which system each transaction belongs to, and prepares what each system will receive — the message, or only that system's transactions — signed by the bank's HSM. It stores all of that and only then acknowledges the message to Bacen. Each system pulls its own messages from the proxy with the usual stream endpoints.

```
System A ──:5201──┐                         ┌──► POST /in/{ispb}/msgs ──► real SPI
                  ├─► SpiProxyApi ──────────┤
System B ──:5202──┘        ▲                └──◄ per-system queue (SQL)
                           │                              ▲
admin ─────:5203───────────┘        SpiInboundWorker ─────┘◄── GET /out/{ispb}/stream ── real SPI
```

The listener a request arrives on identifies the caller: one port per system, a third for administration, metrics and Swagger. Outside Development every listener is TLS with a required client certificate, checked against that listener's own thumbprint list.

## Routing of inbound messages

Each message type has an ordered chain of resolvers; the first one that can decide wins. The decision is stored in `SpiInboundMsg.RouteSource` and exported in the `spi_v2_inbound_routed_total` metric.

| Resolver | Decides when |
|---|---|
| `SentByOriginalMsgId` | the message names a message one of the systems sent → that system |
| `SentByPiResourceId` | a rejection names the `PI-ResourceId` Bacen gave to a sent message |
| `TxAffinity` | its EndToEndId / RtrId / recurrence id is already bound to a system (first writer wins, sent or received) |
| `DocumentRegistry` | our customer's document is in `TB_DADOS_CONVIVENCIA` → System B, otherwise System A |
| `EchoData` | a pibr.002 carries the content a system sent in a pibr.001 |
| `Broadcast` | always: both systems |
| `Default` | always: `Routing:DefaultSystem` (System A) |

Built-in chains (`RoutingTable.Defaults`), checked against the examples and schemas of the Bacen SPI catalog 5.13.1 (`tests/Catalog`, `SpiCatalogTests`):

| Type | Chain | What links it (catalog) |
|---|---|---|
| pacs.008 | TxAffinity → DocumentRegistry (creditor) → Default | `Cdtr/Id/PrvtId/Othr/Id`, per transaction |
| pacs.002 | TxAffinity → Default | `OrgnlInstrId` (EndToEndId, or RtrId for a return). It has **no** `OrgnlMsgId` |
| pacs.004 | TxAffinity → Default | `OrgnlEndToEndId`; introduces `RtrId` |
| camt.055, camt.029 | TxAffinity → Default | `OrgnlEndToEndId` only |
| camt.025 | SentByOriginalMsgId → TxAffinity → Default | `RctDtls/OrgnlMsgId/MsgId` (the trck.002) |
| pibr.002 | EchoData → Default | `EchoTxInf/OrgnlData` |
| admi.002 | SentByOriginalMsgId → SentByPiResourceId → Default | `RltdRef/Ref`: a MsgId, or a PI-ResourceId when the message was unreadable |
| admi.004 | Broadcast | nothing |
| camt.052, camt.053, camt.054 | SentByOriginalMsgId → Broadcast | `Rpt/Id`, `Stmt/Id`, `Ntfctn/Id` = the camt.060; zeros when unsolicited |
| reda.016 (and other reda.\*), camt.014, trck.\* | SentByOriginalMsgId → Broadcast | `MsgHdr/OrgnlBizInstr/MsgId`; camt.014, reda.017, reda.041 name no request |
| pain.009, pain.013 | TxAffinity → DocumentRegistry (debtor) → Default | `MndtId`; pain.013 also `EndToEndId` |
| pain.011, pain.012 | TxAffinity → DocumentRegistry (own side) → Default | `MndtId`; travel in both directions |
| pain.014 | TxAffinity → Default | `OrgnlEndToEndId` (`OrgnlMsgId` is zeros) |
| anything else, or unreadable | SentByOriginalMsgId → Broadcast | |

(The chains in code also start with `SentByOriginalMsgId` where the table omits it; it is harmless where the type names no message.)

**Several transactions in one message.** A pacs.008, pacs.002, pacs.004 (and pain.009/012/013/014, camt.025) can carry many transactions. Each transaction is routed on its own; if they all go to the same system, so does the message. If they differ, the message is **split**: each system receives a message with the same header and ids but only its own transactions (`GrpHdr/NbOfTxs` adjusted), and `RouteSource` is `Split`. Each transaction is bound to the system that received it, so its follow-ups go there too.

`WhenOwnersDiffer` can change this per type: `Split` (default), `AllOwners` (the whole message to every owner) or `DefaultSystem` (the whole message to the default system); the last two record `RouteSource = Mixed`.

**Own side.** For pain.011 and pain.012 the bank can be on either side of the mandate. The customer is the party whose agent (`DbtrAgt`/`CdtrAgt`) is the bank the message is addressed to (`AppHdr/To`).

A chain can be replaced per type in configuration, without code changes:

```json
"Routing": {
  "DefaultSystem": "SystemA",
  "Rules": { "camt.054": { "Chain": ["SentByOriginalMsgId", "Default"] } }
}
```

> **What the catalog cannot confirm.**
> - Whether Bacen really delivers transfers for different customers in one pacs.008. The schema allows it and the examples show 10 transactions; `route_source="Split"` shows how often it happens.
> - "Own side" assumes `AppHdr/To` is the bank's ISPB on messages received from Bacen. The pain.011/pain.012 examples only show the leg from a PSP to the SPI. When the side cannot be told the message goes to the default system; in practice the mandate is usually already bound by `MndtId`.
> - A pacs.002 for a return carrying the RtrId in `OrgnlInstrId` follows from the schema pattern (`[E|D]…`); the catalog has no such example.

## Guarantees and their limits

- A route is written **before** a message is forwarded, so a response cannot arrive before the proxy knows its sender. If the route cannot be written the system gets 503 and nothing is forwarded.
- Nothing is retried towards Bacen. A timeout (`Forward:TimeoutSeconds`, 30 s) answers 504; the system decides whether to resend.
- An inbound batch is acknowledged to Bacen only after every message in it is committed to SQL. A crash before that means redelivery, deduplicated by `PI-ResourceId`.
- SQL is the delivery queue. Redis only wakes long-polling streams; losing it adds up to `DeliveryStream:PollIntervalMs` of latency and nothing else.
- A pulled batch is leased to its stream for `DeliveryStream:LeaseSeconds`. Following `PI-Pull-Next` (or `DELETE`) acknowledges it; otherwise it is delivered again, so systems must tolerate duplicates.
- Kafka carries audit events only (`v2.spi.audit.sent`, `v2.spi.audit.received`, ids only), published from an outbox. Kafka being down does not affect messages.
- **Inbound messages are signed by the bank, not by Bacen.** The worker verifies Bacen's signature (HSM `VerifyPIX`), removes it, and signs what each system receives with the bank's HSM key (`SignPIX`). Both systems must therefore trust the bank's HSM certificate for the messages they pull, and they can no longer check Bacen's signature themselves — the proxy is where that check happens. What Bacen sent is kept unchanged, with its signature, in `SpiInboundMsg.Payload`; what each system received is in `SpiInboundDelivery.Payload`.
- **A message that cannot be vouched for is quarantined.** If Bacen's signature does not verify (`RouteSource = InvalidSignature`) or the content is not XML (`Unreadable`), the message is stored, delivered to no system, counted in `spi_v2_inbound_quarantined_total{reason}` and logged as an error; the stream moves on. There is no re-drive: someone has to look at the row.
- **The HSM is now part of receiving.** If it is unavailable nothing can be signed, so nothing is acknowledged to Bacen: messages wait there (none is lost) and received transfers will time out. Each inbound message costs one verification and one signature (one more signature per extra part when split). Messages of a batch are signed one after the other; if that becomes the bottleneck, raise `InboundPull:Streams`.
- **Outside Production the signature is a stand-in.** Development (self-signed PFX in `certs/dev`) and the software HSM client used in other non-production environments produce a plain enveloped signature, not the SPI signature profile (three references, exclusive canonicalization, issuer/serial key info), and can only verify their own signatures — so verification of real Bacen messages needs the real HSM. Only Production's `SignPIX`/`VerifyPIX` are the real thing.
- **Outbound messages are passed through as signed by their author.** The proxy does not sign what systems send; each system must sign with the key registered at Bacen.
- A change to `TB_DADOS_CONVIVENCIA` takes effect when each process reloads its snapshot — up to `Registry:RefreshSeconds` (5 s) later in the worker, which is the process that routes.
- Stream ids are not tracked: `DELETE` and `GET` on an unknown stream id succeed (acknowledging nothing) instead of answering 410.

## Layout

| Path | Content |
|---|---|
| `src/Domain`, `src/Application` | routing engine, use cases, ports |
| `src/Infrastructure` | SQL repositories (and their in-memory stand-ins), parser, SPI gateway (HSM / HTTP), Redis notifier, pull loop, outbox, cleanup |
| `src/SpiProxyApi` | the three listeners, SPI endpoints, admin API |
| `src/SpiInboundWorker` | pulls from Bacen, publishes the audit outbox, purges expired rows — run exactly one kind of this host |
| `tools/BacenSpiSimulator` | stand-in for the real SPI for local runs and tests: HTTP host over the simulator in `src/Infrastructure/Simulation` |
| `infra/sql` | the proxy's tables (idempotent scripts) |
| `infra/dev` | local-only copy of `TB_DADOS_CONVIVENCIA` |

There is a single database (`ConnectionStrings:SqlServer`): the proxy's tables are created next to `TB_DADOS_CONVIVENCIA`, which already exists there. v2 reads that table and edits its rows through the admin API but never creates or alters it. Locally the database is `DB_COEXISTENCE_V2`; in a shared environment point the connection string at the database that holds the table and apply `infra/sql` to it (the scripts only add tables named `Spi*`, none of which the first solution uses).

## Running locally

Requires the .NET 8 SDK and Docker.

```bash
make infra-up              # SQL Server :14330, Redis :6380, Kafka :9193, Jaeger :16687
make migrate               # DB_COEXISTENCE_V2
make migrate-dev-registry  # local only: TB_DADOS_CONVIVENCIA in the same database
make run-simulator         # fake SPI on :5290
make run-api               # System A :5201, System B :5202, admin :5203
make run-worker            # pulls from the simulator (Development enables InboundPull)
```

```bash
# System B sends; the simulator replies with a pacs.002, which only System B can pull.
curl -i -X POST -H 'Content-Type: application/xml' --data-binary @pacs008.xml http://localhost:5202/api/v1/in/12345678/msgs
curl -i -H 'Accept: multipart/mixed' http://localhost:5202/api/v1/out/12345678/stream/start

# Assign a customer to System B, then make "Bacen" send a transfer.
curl -X POST -H 'Content-Type: application/json' \
     -d '{"conta":"12345","agencia":"0001","documento":"111.222.333-44"}' http://localhost:5203/admin/v1/system-b-documents
curl -X POST --data-binary @received-pacs008.xml http://localhost:5290/sim/inbound
```

Swagger is at `http://localhost:5203/swagger`, metrics at `http://localhost:5203/metrics`. In Development the listeners are plain HTTP without client certificates.

### Without Docker: in-memory storage and simulated SPI

For a quick try-out, the proxy runs alone — no SQL Server, Redis, Kafka or SPI endpoint:

```bash
make run-inmemory          # API + pull loop + simulated SPI in one process; same ports as above
```

`make run-inmemory` starts `SpiProxyApi` with `Storage:Provider=InMemory` (launch profile `inmemory`). In this mode:

- routes, affinities, inbound messages, the delivery queue and the System B registry live in the memory of the API process, behind the same repository interfaces as the SQL ones (`Persistence/InMemory`);
- the API runs the worker's services itself (pull loop, cleanup), because another process could not see its memory — `SpiInboundWorker` refuses to start with this provider;
- **no request leaves the process towards the SPI**: forwards and pulls are answered by the same simulator the `BacenSpiSimulator` tool serves over HTTP (`Infrastructure/Simulation`), through `SimulatedSpiGateway`. `SpiGateway:Transport` and `SpiGateway:BaseUrl` are ignored; the `Simulator` section configures it. Its developer endpoints (`/sim/inbound`, `/sim/sent`, `/sim/status`) are on the admin listener;
- audit events are written to the log instead of Kafka, and long-poll wake-ups stay in process instead of going through Redis;
- the registry starts empty: add customers through the admin API;
- **nothing survives a restart.** The provider is refused in Production.

The same curl commands as above work, except that "Bacen" sends through the admin port: `curl -X POST --data-binary @received-pacs008.xml http://localhost:5203/sim/inbound`.

## Tests

```bash
make test
```

| Project | Needs Docker | Covers |
|---|---|---|
| `Domain.Tests`, `Application.Tests` | no | routing table and resolvers, forward / inbound / pull use cases |
| `Infrastructure.Tests` | yes (SQL Server) | parser, repositories under concurrency, leases, gateways, pull loop; the in-memory provider (`InMemoryStorageTests`, no Docker) |
| `Integration.Tests` | yes (SQL Server, Redis) | API + worker + simulator end to end; `InMemoryProxyTests` runs the in-memory mode (no Docker) |

The Docker-free subset: `dotnet test ConvivenciaPix.V2.sln --filter "FullyQualifiedName~InMemory"`.

## Configuration

| Section | Keys |
|---|---|
| `Storage` | `Provider`: `SqlServer` (default) or `InMemory` (single process, no SQL Server / Redis / Kafka, SPI simulated in process; refused in Production) |
| `Simulator` (in-memory mode, and the simulator tool) | `LongPollSeconds`, `MaxMessagesPerMultipart`, `AutoReply` |
| `ConnectionStrings` | `SqlServer` (the one database: proxy tables + `TB_DADOS_CONVIVENCIA`), `Redis` (optional) |
| `Listeners` | `SystemAPort`, `SystemBPort`, `AdminPort`, `Https`, `Certificate:{Path,Password}`, `{SystemA,SystemB,Admin}:TrustedThumbprints` |
| `Spi` | `Ispb` — when set, any other ISPB in the URL is refused |
| `SpiGateway` | `BaseUrl`, `Transport` (`Http`/`Dinamo`), `MaxConcurrentForwards`, `MtlsKeyId`, `MtlsCertId`, `ServerCertChainId`, `UseGzip`, `VerifyHostName` |
| `Dinamo` | `Host`, `UserId`, `Password`; for inbound signing (worker): `KeyId`, `CertId` (sign), `ChainId`, `Crl` (verify Bacen) |
| `InboundSigning` (worker) | `VerifyBacenSignature` (default true; false in Development, where the simulator does not sign) |
| `Forward` | `TimeoutSeconds` |
| `DeliveryStream` | `LongPollSeconds`, `MaxMessagesPerMultipart`, `LeaseSeconds`, `PollIntervalMs` |
| `Routing` | `DefaultSystem`, `Rules`, `UnknownTypeChain` |
| `Registry` | `RefreshSeconds` |
| `InboundPull` (worker) | `Enabled`, `Ispb`, `Streams`, `PullTimeoutSeconds`, `ErrorBackoffMs` |
| `Audit` (worker) | `Enabled`, `BootstrapServers`, `PollSeconds`, `BatchSize` |
| `Cleanup` (worker) | `Enabled`, `IntervalMinutes`, `MessageRetentionDays` (30), `AffinityRetentionDays` (90), `OutboxRetentionDays`, `BatchSize` |

Outside Development the gateway performs mTLS through the Dinamo HSM (`DinamoNetSdkClient` in Production, a software PFX client in other environments), and an empty thumbprint list on a listener trusts nobody.

## Cut-over

1. Deploy the API and the worker with `InboundPull:Enabled=false`. Fill the thumbprint lists, `Spi:Ispb` and the worker's `Dinamo` signing ids. Make both systems trust the bank's HSM certificate for the messages they pull — System A included, which until now only saw Bacen's signature.
2. Point System B at its listener. It can send; it receives nothing yet.
3. Stop the first solution's mirroring to System B (CDC correlation and proxy workers), so System B is not fed twice.
4. In one maintenance step: point System A at its listener, **stop System A pulling from Bacen directly**, then set `InboundPull:Enabled=true`. Two consumers on the same SPI stream would split messages between them at random.
5. Watch `spi_v2_inbound_quarantined_total`: it must stay at zero. Anything there right after cut-over most likely means `Dinamo:ChainId` does not match Bacen's signing chain, and those messages are reaching nobody.
6. Watch `spi_v2_inbound_routed_total{route_source="Default"}`. Right after cut-over it counts responses to messages System A sent before the proxy existed (they go to System A, which is correct); it should decay to zero. A sustained value means a message type is not being matched.
7. Move customers to System B through the admin API.

Returns (pacs.004) of payments System A made before cut-over have no affinity and go to System A by default. If System B will ever own such payments, seed `SpiTxAffinity` from System A's sent table first.
