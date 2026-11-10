<div align="center">

# ClearPay Ledger

**A double-entry wallet backend with idempotent money APIs, simulated 3-D Secure card payments, a separate reconciliation service, a Razor web UI and a Flutter client.**
<br/>
<sub>İdempotent para API'leri, simüle 3-D Secure kart ödemeleri, ayrı bir mutabakat servisi, Razor web arayüzü ve Flutter istemcisi olan çift taraflı kayıt (double-entry) tabanlı cüzdan backend'i.</sub>

<br/>

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![SQL Server 2022](https://img.shields.io/badge/SQL%20Server-2022-CC2927?style=flat-square&logo=microsoftsqlserver&logoColor=white)
![Flutter](https://img.shields.io/badge/Flutter-Dart%203-02569B?style=flat-square&logo=flutter&logoColor=white)
[![CI](https://github.com/HalilMertDeveli/clearpay-ledger/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/HalilMertDeveli/clearpay-ledger/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
![Status](https://img.shields.io/badge/status-prototype%20%2F%20portfolio%20demo-orange?style=flat-square)

<p><a href="#english">English</a> · <a href="#turkce">Türkçe</a></p>

</div>

> [!WARNING]
> **Portfolio demo, not a banking product.** ClearPay is not a licensed bank, payment institution or e-money issuer. No real money moves: the bank gateway, the payment service providers and the 3-D Secure issuer page are simulated, and amounts are demo Turkish lira (TRY).
> <br/>**Portföy demosudur, bankacılık ürünü değildir.** ClearPay lisanslı bir banka, ödeme kuruluşu veya elektronik para kuruluşu değildir. Gerçek para hareketi yoktur: banka gateway'i, ödeme sağlayıcıları (PSP) ve 3-D Secure banka sayfası simüle edilmiştir; tutarlar demo Türk lirasıdır (TRY).

| | |
|---|---|
| **Type · Tür** | Wallet API + Razor web UI + Flutter client · Cüzdan API'si + Razor web arayüzü + Flutter istemcisi |
| **Status · Durum** | Prototype / portfolio demo, runs locally · Prototip / portföy demosu, yerelde çalışır |
| **Stack · Teknoloji** | .NET 8, ASP.NET Core (Razor Pages + Web API), EF Core 8, SQL Server 2022 (T-SQL stored procedures), Identity + JWT, Hangfire, Redis, RabbitMQ, SignalR, Serilog + OpenTelemetry, Flutter |
| **Core ideas · Temel fikirler** | Double-entry ledger, derived balances, `Idempotency-Key`, single-transaction posting, transactional outbox, 3-D Secure, PSP adapters, nightly reconciliation, RabbitMQ retry / DLQ / inbox, end-to-end tracing |
| **Tests · Testler** | xUnit + `WebApplicationFactory` (SQLite, SQL Server and RabbitMQ), Flutter widget tests, GitHub Actions |
| **UI languages · Arayüz dilleri** | Turkish (default), English, German, French · Türkçe (varsayılan), İngilizce, Almanca, Fransızca |

---

<a id="english"></a>

## English

- [Problem & Solution](#en-problem)
- [Features](#en-features)
- [Architecture](#en-architecture)
- [Core Banking Concepts](#en-concepts)
- [Double-Entry Ledger](#en-ledger)
- [Idempotency](#en-idempotency)
- [Transaction Consistency](#en-consistency)
- [Card Payments: 3-D Secure (simulated)](#en-3ds)
- [Card Payments: PSP Adapters, Capture, Refund, Void](#en-psp)
- [Operations Service: Notifications and Reconciliation](#en-operations)
- [Messaging: RabbitMQ Retry, Dead-Letter Queue, Inbox](#en-messaging)
- [T-SQL: Stored Procedures, Covering Indexes, Measured Plans](#en-tsql)
- [Observability: One Payment, One Trace, Across Both Services](#en-observability)
- [API Design](#en-api)
- [Technology Stack](#en-stack)
- [Repository Structure](#en-structure)
- [Tests & CI](#en-tests)
- [Installation](#en-install)
- [Demo Screenshots](#en-screenshots)
- [Security Considerations](#en-security)
- [Limitations](#en-limitations)
- [License](#en-license)

<a id="en-problem"></a>

### Problem & Solution

**Problem.** The classic wallet bug is `UPDATE Wallet SET Balance = Balance - @amount`. It loses
history, cannot be audited, double-charges when a mobile client retries after a timeout, and
leaves money half-moved when the process dies between the debit and the credit.

**Solution.** ClearPay never stores a balance. Every movement is a pair of signed ledger rows
(one debit, one credit, summing to zero) written in **one SQL Server transaction** together with
the transfer record, the idempotency key, an audit row and an outbox message. The balance is the
sum of a wallet's ledger rows. A replayed request with the same `Idempotency-Key` returns
**409 Conflict** and posts nothing.

<a id="en-features"></a>

### Features

| Area | Capability | Where |
|---|---|---|
| Ledger | Balanced debit/credit pairs, derived balance, no-overdraft check, reversal (refund) pair | `src/ClearPay.Domain/Ledger/LedgerPair.cs` |
| Transfers | Serializable transaction: ledger pair + transfer + idempotency key + audit + outbox | `src/ClearPay.Infrastructure/Persistence/SqlTransferExecutor.cs` |
| Top-up / withdraw | Pairs against a treasury (clearing) wallet via the simulated bank gateway | `SqlFundingExecutor.cs`, `src/ClearPay.Infrastructure/Banking/` |
| Idempotency | `Idempotency-Key` header, primary-key uniqueness, SHA-256 request fingerprint | `SqlIdempotencyStore.cs`, `src/ClearPay.Web/Controllers/TransfersController.cs` |
| Outbox and messaging | Committed outbox rows published to RabbitMQ with publisher confirms; retry, DLQ and inbox deduplication | `SqlOutboxProcessor.cs`, `src/ClearPay.Messaging/` |
| 3-D Secure | Fake issuer ACS page, HMAC-signed messages in both directions, explicit state machine | `/api/3ds`, `/fake-acs`, `ThreeDsSigner` |
| Card payments | Three fake PSPs behind one interface; authorize, capture, partial refund, void (idempotent) | `IPaymentServiceProvider`, `PspRouter`, `/api/payments` |
| Reconciliation | Nightly Hangfire job matches PSP settlement files in the separate Operations service | `src/ClearPay.Operations/` |
| T-SQL | Stored procedures, covering indexes, measured execution plans | `LedgerProcedures.sql`, `docs/sql/` |
| Observability | Serilog + OpenTelemetry; one trace per payment across both services | `src/ClearPay.ServiceDefaults/`, `docs/RUNBOOK-failed-payment.md` |
| Receipts | JSON and generated PDF receipts by correlation id | `ReceiptsController.cs`, `src/ClearPay.Infrastructure/Documents/SimplePdfReceiptRenderer.cs` |
| Auth | ASP.NET Identity (cookie for Razor), JWT for API/mobile, optional Google/Apple/Firebase | `src/ClearPay.Infrastructure/Identity/` |
| Admin | Freeze/unfreeze wallets, outbox view and re-queue, audit search | `AdminApiController.cs`, `SqlAdminPanel.cs`, `Pages/Admin.cshtml` |
| Linked cards | Stores last four digits, scheme and label only | `CardsController.cs`, `src/ClearPay.Application/Funding/CardBindingParser.cs` |
| Realtime | SignalR `/hubs/wallet` sends refresh hints, never balances | `src/ClearPay.Web/Realtime/` |
| Caching | Optional Redis wallet-summary cache, invalidated after commit | `src/ClearPay.Infrastructure/Caching/` |
| Web UI | Razor Pages: overview, transfer, cards, top-up/withdraw, movements, receipt, admin | `src/ClearPay.Web/Pages/` |
| Localization | `tr` (default), `en`, `de`, `fr` resources | `src/ClearPay.Web/Localization/`, `src/ClearPay.Web/Resources/Localization/` |
| Mobile | Flutter JWT client with QR, PDF receipt sharing, SignalR refresh | `mobile/clearpay/lib/` |

<a id="en-architecture"></a>

### Architecture

Four projects in a dependency-inverted (onion / clean) layout. The rules are enforced by
`ArchitectureTests` (for example: Domain has no package references, Web pages never compute ledger
math or instantiate gateways).

```mermaid
flowchart LR
    subgraph Clients
        WEB["Razor Pages UI<br/>(cookie auth)"]
        MOB["Flutter app<br/>(JWT)"]
    end

    subgraph Host["ClearPay.Web (ASP.NET Core 8, single host)"]
        API["JSON API controllers<br/>/api/transfers, /api/topup, /api/withdraw,<br/>/api/wallet, /api/movements, /api/receipts"]
        PAGES["Razor PageModels"]
        HUB["SignalR /hubs/wallet<br/>(refresh hint only)"]
        SWAG["Swagger /swagger"]
    end

    subgraph App["ClearPay.Application"]
        PORTS["Ports: ITransferExecutor, IFundingExecutor,<br/>IWalletReader, IIdempotencyStore,<br/>IBankGateway, IOutboxPublisher"]
    end

    subgraph Dom["ClearPay.Domain"]
        LEDGER["LedgerPair, LedgerEntry, Wallet,<br/>Transfer, IdempotencyRecord,<br/>AuditLog, OutboxMessage"]
    end

    subgraph Infra["ClearPay.Infrastructure"]
        EXEC["SqlTransferExecutor /<br/>SqlFundingExecutor (EF Core)"]
        GW["Rest/Soap BankGateway (simulated)"]
        OUTBOX["Hangfire job ->SqlOutboxProcessor"]
        CACHE["Redis wallet-summary cache"]
        IDENT["ASP.NET Identity + JWT issuer"]
    end

    SQL[("SQL Server<br/>ledger + Identity")]
    REDIS[("Redis (optional)")]
    MQ[("RabbitMQ (optional)")]

    WEB --> PAGES
    MOB --> API
    MOB -.-> HUB
    PAGES --> PORTS
    API --> PORTS
    PORTS --> LEDGER
    EXEC --> PORTS
    EXEC --> SQL
    EXEC --> GW
    OUTBOX --> SQL
    OUTBOX --> MQ
    CACHE --> REDIS
    IDENT --> SQL
```

| Project | Holds | Must not hold |
|---------|-------|---------------|
| `ClearPay.Domain` | Ledger types and money rules (`LedgerPair.Create`, `EnsureBalanced`, `NetOf`, `WouldGoNegative`) | EF, HTTP, packages |
| `ClearPay.Application` | Use-case contracts (ports), commands/outcomes, FluentValidation | Connection strings, EF |
| `ClearPay.Infrastructure` | EF Core (SQL Server, SQLite for tests), Identity, JWT, Redis, RabbitMQ, Hangfire, gateways | Razor, CSS |
| `ClearPay.Web` | Composition root, controllers, Razor Pages, SignalR hub | Ledger arithmetic |
| `ClearPay.Messaging` | Shared building block: RabbitMQ topology, consumer host with retry / DLQ, inbox, trace headers | Domain types, tables |
| `ClearPay.ServiceDefaults` | Serilog + OpenTelemetry setup shared by both hosts | Business code |
| `ClearPay.Operations` | Separate service: notifications + nightly reconciliation, own database (schema `ops`) | Any reference to the ledger host or its database |
| `mobile/clearpay` | Flutter client (JWT); no local balance | Any money logic |

Redis and RabbitMQ are optional: without Redis the summary is read from SQL. With an empty
`ConnectionStrings:RabbitMq` the outbox only logs events; with a configured broker that is down,
events wait in the outbox and are published when it comes back. SQL Server is the only source of truth.

<a id="en-concepts"></a>

### Core Banking Concepts Demonstrated

- **Double-entry bookkeeping**: every movement is one debit (`-amount`) and one credit (`+amount`) sharing a `PairId` and `CorrelationId`.
- **Derived balances**: no `Balance` column exists on `Wallet`; balance = sum of the wallet's signed entries.
- **No overdraft**: a debit is rejected if the ledger net would go below zero (checked inside the transaction).
- **Idempotency**: `Idempotency-Key` header, unique index, request fingerprint, 409 on replay.
- **Atomic posting**: ledger pair + transfer + idempotency key + audit + outbox commit or roll back together.
- **Transactional outbox**: integration events are rows in the same transaction; a background job publishes them later.
- **Audit trail**: who, what, when and correlation id for every posting.
- **Account freeze**: frozen wallets cannot send or withdraw (403); incoming credits still post.
- **Clearing account**: top-ups and withdrawals are pairs against a treasury wallet, so even external money keeps the ledger balanced.
- **Gateway timeout handling**: a timed-out top-up posts no ledger rows, records a pending outbox row and is answered with 202; retrying with the same key does not double-credit.
- **Reversal model**: history is never edited; card refunds post a reverse pair (customer -> treasury) and a refund the PSP declines posts the pair back (`/api/payments/{id}/refunds`). Wallet-to-wallet transfers still have no refund endpoint.
- **3-D Secure (simulated)**: merchant -> gateway -> fake issuer ACS page -> HMAC-signed callback, with an explicit state machine (`Initiated`, `Challenge`, `Authenticated`, `Failed`, `TimedOut`).
- **Receipts**: every posting is retrievable by correlation id as JSON or a generated PDF.

<a id="en-ledger"></a>

### Double-Entry Ledger

```text
Transfer of 25.00 from A to B, correlation C, pair P

LedgerEntry  WalletId  Amount   PairId  CorrelationId  Kind
-----------  --------  -------  ------  -------------  --------
#1           A         -25.00   P       C              Transfer
#2           B         +25.00   P       C              Transfer
                       ------
                         0.00   (every pair, and therefore the whole ledger, sums to zero)
```

- `LedgerPair.Create` is the only constructor of entries used by the executors. It rejects
  non-positive amounts and identical wallets and calls `EnsureBalanced` on the result.
- The database adds its own guard: `CK_LedgerEntry_Amount_NotZero`, `decimal(18,2)` amounts,
  restrict-delete foreign keys, and a covering index on `(WalletId, CreatedAt) INCLUDE (Amount, Kind, CorrelationId)`.
- Balance = `SUM(Amount) WHERE WalletId = @wallet`. Wallet summaries may be cached in Redis but
  are invalidated after every commit and never written back to SQL.

Why no balance column? A stored balance is a second source of truth that can drift from the
entries. Deriving it makes "balance equals the sum of entries" true by construction; the cost is a
read over the wallet's rows, acceptable at demo scale (see Limitations).

<a id="en-idempotency"></a>

### Idempotency

`POST /api/transfers` requires an `Idempotency-Key` header (max 128 characters).

1. Before opening a transaction, look up the key. Found: return **409** with the original
   `transferId` and `correlationId`; nothing is written.
2. Inside the transaction, look up the key again, then insert the key together with the ledger
   pair. The key is the primary key of `IdempotencyRecord`, so two racing requests cannot both
   commit: the loser gets a unique violation and is answered with 409.
3. The record stores a SHA-256 fingerprint of recipient, amount and description. Reusing a key
   with a different body is reported as `KeyPayloadMismatch` (also 409) and never debits.

Why 409 and not "200 with the cached response"? Both are defensible. 409 makes the replay explicit
to the client ("already processed, not charged again") and still returns the original ids, so a
client can open the existing receipt. Top-up and withdrawal follow the same rules with their own
key scope.

```mermaid
sequenceDiagram
    participant C as Client
    participant API as TransfersController
    participant EX as SqlTransferExecutor
    participant DB as SQL Server
    C->>API: POST /api/transfers with Idempotency-Key K
    API->>EX: TransferCommand, sender taken from JWT
    EX->>DB: look up key K
    alt key already stored
        EX-->>API: Replay
        API-->>C: 409 with original transferId
    else new key
        EX->>DB: BEGIN TRAN (Serializable)
        EX->>DB: re-check K and overdraft
        EX->>DB: insert ledger pair, transfer, key, audit, outbox
        EX->>DB: COMMIT
        EX-->>API: Posted
        API-->>C: 201 with transferId and correlationId
    end
```

<sub>Transfer request flow with an idempotency key, following `SqlTransferExecutor`.</sub>

<a id="en-consistency"></a>

### Transaction Consistency

- Transfers and fundings run in a single EF Core transaction at **Serializable** isolation. The
  overdraft check reads the sender's entries inside that transaction, so two concurrent debits
  cannot both see the old balance.
- Under contention SQL Server may pick a deadlock victim. That surfaces as **503 "nothing was
  charged, try again"**; the client retries with the same key and either succeeds or gets 409.
- Any failure before commit rolls back the ledger pair, transfer row, idempotency key, audit row
  and outbox row together, so a failed attempt does not burn the key.
- External side effects never happen inside the transaction. Cache invalidation and SignalR
  notifications run after commit; RabbitMQ publishing happens later from the outbox. For
  top-ups, the simulated bank call happens **before** the ledger transaction and only a
  successful response is posted.
- The outbox worker (Hangfire, every minute) publishes Pending rows with publisher confirms; a failed
  publish keeps the row Pending (attempt counter), after 5 failures it is `Failed` and can be
  re-queued from the admin API. Delivery is at-least-once; consumers deduplicate on `MessageId`
  with an inbox table (see Messaging).

<a id="en-3ds"></a>

### Card Payments: 3-D Secure (simulated)

A card payment is authenticated before any money moves. Everything here is a simulation: the
"issuer" is an in-process page called **SimBank**, no card network or bank is contacted, and only
the last four digits of the (test) card number are stored.

```mermaid
sequenceDiagram
    participant M as Merchant (app / API client)
    participant G as Gateway (/api/3ds)
    participant A as Fake ACS (/fake-acs, "SimBank")
    M->>G: POST /api/3ds/sessions (JWT, Idempotency-Key, amount, test PAN)
    G->>G: Luhn check, keep last 4, status Initiated -> Challenge
    G-->>M: 201 { sessionId, status, acsUrl = /fake-acs?creq=<signed> }
    M->>A: open acsUrl in a browser / web view
    A->>A: verify CReq HMAC, show amount + card, ask OTP (demo: 123456)
    A-->>M: auto-submitting form with the signed CRes
    M->>G: POST /api/3ds/callback (form: sessionId, transStatus, eci, authValue, issuedAt, nonce, signature)
    G->>G: verify HMAC + time window, compare-and-set Challenge -> Authenticated | Failed | TimedOut
    G-->>M: 200 { status }
```

| State | Reached when |
|-------|--------------|
| `Initiated` | Session row created (never visible for long; the fake directory server decides immediately) |
| `Challenge` | Card is enrolled: the cardholder must answer the ACS challenge |
| `Authenticated` | ACS returned `transStatus=Y` with an authentication value (ECI 05) |
| `Failed` | Wrong OTP, cardholder cancelled, or test card `...0002` (issuer refuses without a challenge) |
| `TimedOut` | No valid result before the deadline (10 minutes by default); set by the Hangfire job `clearpay-3ds-expiry` every minute or lazily on read/callback |

How the callback is trusted:

- **Signed in both directions.** The gateway signs the challenge request (CReq) and the ACS signs
  the challenge result (CRes) with HMAC-SHA256 over every field (`ThreeDsSigner`). The two
  messages use different prefixes so one can never be replayed as the other. Comparison is
  constant-time. A forged or modified result is answered with **401** and changes nothing.
- **Freshness.** `issuedAt` must be within 5 minutes of the server clock (**400** otherwise).
- **Exactly one transition.** The state change is `UPDATE ... WHERE Id = @id AND Status = @expected`
  committed together with its audit row and outbox event (`clearpay.3ds.authenticated`,
  `.failed`, `.timedout`). A browser double-submit of the same result returns 200
  `AlreadyApplied`; a different result for a finished session returns **409**; a result after the
  deadline returns **410** and the session becomes `TimedOut`. The SQL Server test fires eight
  conflicting callbacks in parallel and asserts exactly one transition and one outbox event.
- **The callback is anonymous on purpose.** It is posted by the browser from the bank page, so it
  carries no JWT or cookie; trust comes only from the signature and the state machine.
- **Key.** `ThreeDs:AcsSharedSecret` (at least 32 bytes). Development falls back to a public
  placeholder; in every other environment the 3-D Secure endpoints fail (500) until a key is configured.

Authentication alone never posts to the ledger; capturing the payment is a separate step.

<a id="en-psp"></a>

### Card Payments: PSP Adapters, Capture, Refund, Void

An `Authenticated` 3-D Secure session can pay for exactly one card payment, which funds the
customer's wallet. The payment service talks to providers only through
`IPaymentServiceProvider` (authorize, capture, refund, void). Three fake providers sit behind it,
each with its own wire format, so the adapter layer does real mapping work:

| Adapter | Wire format (simulated in-process) | Documented fault trigger |
|---------|-----------------------------------|--------------------------|
| `simbank-rest` (default) | JSON, decimal strings, `APPROVED` / `DECLINED` | authorize `x.51` -> declined `51`; refund `x.52` -> declined `R52` |
| `simbank-soap` | SOAP 1.1 XML envelope, `ResponseCode` `00` = approved | authorize `x.05` -> declined `05` |
| `simpay` | minor units (kuruş as integers), `succeeded` / `failed` | any amount `x.99` -> executed by the PSP but the **response is lost** (caller sees a timeout) |

None of them contacts a network. Their own "bank side" journal is the `sim.PspTransaction` table,
written only by the simulators through a separate DbContext scope; settlement files for
reconciliation are produced from it, never from the ledger. Adding a provider means one more
`IPaymentServiceProvider` registration (`PspRouter` picks it by name).

| Operation | Ledger effect | Rule |
|-----------|---------------|------|
| Authorize | none | 3-D Secure session must be `Authenticated`, owned by the caller and unused (claimed with a compare-and-set, unique index `UX_CardPayment_ThreeDsSessionId`) |
| Capture (default on create) | treasury -> customer, kind `CardPayment` | posted only after the PSP approved; once per payment |
| Refund (full or partial) | customer -> treasury, kind `Refund` | sum of refunds <= captured amount; the wallet must still hold the amount; a PSP decline posts the reverse pair |
| Void | none | only before capture |

How an operation is made idempotent end to end:

1. **Reserve in SQL** (Serializable): `Idempotency-Key` + request fingerprint, a `PaymentOperation`
   row with a **deterministic provider key** (`<paymentId>-auth`, `-capture`, `<operationId>-refund`),
   and the money rule (for refunds the wallet debit is posted here, so two racing refunds cannot
   both pass the check).
2. **Call the PSP outside any transaction** with that provider key. Every fake PSP stores the key
   and answers a repeat with the original result instead of executing again.
3. **Record the answer** in one transaction: operation status, payment status, ledger pair, audit
   row and outbox event (`clearpay.payment.authorized`, `.captured`, `.declined`, `.refunded`,
   `.refund_failed`, `.voided`).

A PSP timeout leaves the operation `Pending` and returns **202**. Retrying with the same
`Idempotency-Key` re-drives it with the same provider key: the `simpay` lost-response test needs
three calls to finish authorization and capture and still ends with one PSP authorization, one
PSP capture and one ledger pair. CHECK constraints back the rules in the database
(`CK_CardPayment_Refund_LE_Captured`, `CK_CardPayment_Captured_LE_Amount`).

<a id="en-operations"></a>

### Operations Service: Notifications and Reconciliation (separate process)

Notifications and the nightly reconciliation run in their own ASP.NET Core service,
`src/ClearPay.Operations`, with **their own database** (`ClearPayOps`, schema `ops`). It never opens
the ledger database (an architecture test fails the build if it references `ClearPay.Infrastructure`
or mentions `ClearPayDbContext`); everything it knows arrives over RabbitMQ or from the PSP drop folder.
Before this step the ledger host had no notifications at all (only SignalR refresh hints), so
notifications are new here rather than moved; reconciliation was moved out of the ledger host.

```mermaid
flowchart LR
    subgraph Ledger["ClearPay.Web (ledger host, DB: ClearPay)"]
        PAY["Card payments + fake PSPs"] --> OUT[("Outbox")]
        PSP["PSP end-of-day job 01:00 UTC"]
    end
    OUT -->|"publisher confirms"| EX{{"clearpay.events (topic)"}}
    PSP -->|"{provider}_{yyyyMMdd}.csv"| DROP[/"settlement drop folder"/]
    EX -->|"clearpay.payment.operation.recorded"| PROJ["projection consumer"]
    EX -->|"payment.captured / refunded / declined, 3ds.failed / timedout,<br/>reconciliation.completed"| NOTE["notification consumer"]
    subgraph Ops["ClearPay.Operations (DB: ClearPayOps, schema ops)"]
        PROJ --> P[("ops.PaymentOperation")]
        NOTE --> N[("ops.Notification")]
        JOB["Hangfire 02:30 UTC reconciliation"] --> P
        JOB --> R[("ops.ReconciliationRun / Item")]
    end
    DROP --> JOB
    JOB -->|"clearpay.reconciliation.completed"| EX
```

**What crosses the boundary.** The ledger writes a `clearpay.payment.operation.recorded` outbox event
whenever a PSP operation is created (`Pending`) or answered (`Succeeded` / `Failed`), in the same
transaction as the operation row. The Operations service keeps the last state per operation in
`ops.PaymentOperation` (inbox-deduplicated; a late `Pending` snapshot never overwrites a final one).
The simulated PSPs drop one settlement CSV per provider and day into `Reconciliation:SettlementDirectory`
(written to a `.part` file and renamed, so a reader never sees half a file) - the stand-in for an SFTP
or bucket drop. Both services share only the JWT settings (admin reports) and the messaging building
block `src/ClearPay.Messaging` (topology, consumer host, inbox; no domain types, no tables).

**Notifications.** A consumer turns `payment.captured`, `refunded`, `refund_failed`, `declined`,
`3ds.failed`, `3ds.timedout` and `reconciliation.completed` into rows in `ops.Notification` (the demo
"mailbox"; `LogNotificationSender` only logs - no e-mail, SMS or push provider is called). The inbox
plus a unique `SourceMessageId` mean a redelivered event never notifies twice; the cross-service
RabbitMQ test re-publishes the whole ledger outbox and still sees one notification.

**Reconciliation** (nightly at 02:30 UTC, Hangfire job `clearpay-ops-reconciliation-nightly`, for
**yesterday**; an admin can run any date with `POST /api/reconciliation/runs?date=yyyy-MM-dd` on the
Operations service):

1. **Settlement files** from the drop folder (`SettlementCsv`; malformed lines are reported, a missing
   file is recorded on the run). Two documented faults give the job something to find: amounts ending
   in `.77` are left out of the PSP file, amounts ending in `.78` are settled one kuruş higher.
2. **Our side**: the projection's Capture/Refund operations of that date that did not fail
   (`ops.usp_SettlementCandidates` on SQL Server).
3. **Matching** (`ReconciliationEngine` in `ClearPay.Application`, pure and unit-tested): by provider
   reference, then - for operations whose PSP response was lost and therefore have no reference - by
   type + merchant reference.

| Item kind | Meaning | What an operator does |
|-----------|---------|-----------------------|
| `Matched` | same reference and amount | nothing |
| `MissingInSettlement` | we captured/refunded, the PSP did not settle it | raise with the PSP |
| `MissingInLedger` | the PSP settled something we have no record of | investigate; possible duplicate or foreign transaction |
| `AmountMismatch` | same reference, different amount | raise with the PSP |
| `PendingConfirmedBySettlement` | our operation is still `Pending` (timeout) but the PSP settled it | finish it: retry with the same key |
| `PendingNotInSettlement` | still `Pending` and not settled | most likely never happened at the PSP |

Every item is stored, each mismatch is logged as a warning with provider, reference and amounts, and
the run publishes `clearpay.reconciliation.completed`, which the notification consumer turns into a
report for `operations-team`. That publish is best effort after the commit (the stored run is the
source of truth; the service has no outbox of its own). A date with a completed run is skipped by
the nightly job (Hangfire retries a failed run up to three times); a manual run always creates a new run.

Run it locally next to the ledger host:

```bash
dotnet ef database update --project src/ClearPay.Operations --connection "<ClearPayOps connection string>"   # or let it migrate on start
dotnet run --project src/ClearPay.Operations --launch-profile http    # http://localhost:5163/health
```

Set `ConnectionStrings:Operations`, `ConnectionStrings:RabbitMq`, the same `Jwt:*` values as the
ledger host, and the same `Reconciliation:SettlementDirectory` in both services (default: a
`clearpay-settlements` folder under the OS temp directory).

<a id="en-messaging"></a>

### Messaging: RabbitMQ Retry, Dead-Letter Queue, Inbox

Outbox rows are published to the topic exchange `clearpay.events` (routing key = event type,
`MessageId` = outbox row id, persistent, **publisher confirms**). If the broker is configured but
unreachable or does not confirm, the publisher throws: the row stays `Pending` with `Attempts + 1`
and the batch stops; after 5 failed attempts it becomes `Failed` and an admin can re-queue it.
Only an empty `ConnectionStrings:RabbitMq` switches messaging off (rows are then logged).

Every consumer gets its own queue set, declared idempotently by `RabbitTopology`:

```text
clearpay.events (topic) --clearpay.payment.captured--> clearpay.cashback ----> handler
                                                          |        ^
                       exception, attempt n < max  -------+        | TTL expires, dead-letters back
                                                          v        |
                                     clearpay.cashback.retry.1 (1 s) / .retry.2 (5 s) / .retry.3 (25 s)
                       attempts exhausted, or poison (reject) ---> clearpay.cashback.dlq  (x-last-error, x-death)
```

- **Retry with backoff**: on an exception the consumer publishes a copy with `x-attempt` + 1 to the
  next delay queue and acks the original; when the TTL expires the broker routes it back.
  Defaults: 4 attempts, delays 1 s / 5 s / 25 s (`Messaging:MaxAttempts`, `Messaging:RetryDelaysMs`).
- **Dead-letter queue**: after the last attempt the copy goes to `<queue>.dlq` with the last error in
  `x-last-error`; a message that can never succeed (`PoisonMessageException`, missing `MessageId`)
  is rejected without requeue and the broker dead-letters it there immediately.
- **Consumer idempotency (inbox)**: `InboxProcessor` inserts `InboxMessage (Consumer, MessageId)`
  and the handler's changes in **one transaction**, then the message is acked. A redelivery - outbox
  re-publish, broker redelivery after a crash between commit and ack, or a retry - finds the row and
  is acked without running the handler; two racing deliveries collide on the primary key and the
  loser rolls back its side effects.
- **The consumer that proves it posts money**: `CashbackOnCaptureHandler` (a demo promotion) credits
  1 % of every captured card payment from the `clearpay-promotions` purse to the customer
  (ledger kind `Cashback`). The RabbitMQ tests publish the same message twice, crash a consumer
  after commit but before ack, fail the handler twice before it succeeds, and fail it forever:
  the cashback is posted exactly once (or not at all for the dead-lettered message).
- **Connection**: one long-lived connection with automatic recovery, opened lazily, so the app starts
  without a broker; `/api/health` reports `up`, `down` or `off` live.
- **Trace context**: `traceparent` / `tracestate` travel in the AMQP headers and the consumer starts its
  span as a child of the publisher's (`ClearPay.Messaging` activity source).

<a id="en-tsql"></a>

### T-SQL: Stored Procedures, Covering Indexes, Measured Plans

The hot read paths used to be EF queries that pulled every ledger row of a wallet to the app and
summed it in C#. On SQL Server they now call stored procedures that answer from covering indexes
(SQLite in the fast tests keeps the equivalent LINQ; `LedgerSql` picks one).

| Procedure | Used by | Index it seeks |
|-----------|---------|----------------|
| `dbo.usp_LedgerWalletBalance @WalletId` | overdraft check in transfer, withdraw and card refund (runs **inside** the caller's Serializable transaction) | `IX_LedgerEntry_WalletId_CreatedAt (WalletId, CreatedAt) INCLUDE (Amount, Kind, CorrelationId)` |
| `dbo.usp_LedgerWalletSummary @WalletId, @MonthStart` | `/api/wallet` and the home page: balance, month in/out, last 5 movements in one round trip | same index (aggregate + backward `TOP (5)` range scan, no sort) |
| `dbo.usp_PaymentSettlementCandidates @From, @To` (until the next step) | nightly reconciliation (one day of captures / refunds). Dropped from the ledger when reconciliation moved to the Operations service, which uses the same shape as `ops.usp_SettlementCandidates` on its projection | `IX_PaymentOperation_CreatedAt_Type (CreatedAt, Type) INCLUDE (...)`, now on `ops.PaymentOperation` |

The procedures live in `src/ClearPay.Infrastructure/Persistence/Sql/LedgerProcedures.sql` and are
created by the EF migration `AddLedgerProceduresAndCoveringIndexes`. A narrower covering index also
narrows the key-range locks the Serializable overdraft check takes.

**Before / after, from actual execution plans** (`scripts/sql/Measure-LedgerPlans.ps1`: fresh
database, all migrations, synthetic data - 1,000,000 ledger rows with 100,000 on the measured
wallet, 125,000 payment operations; SQL Server 2022 in Docker; warm cache; median of 5 runs;
reads / CPU / elapsed taken from `RunTimeCountersPerThread` and `QueryTimeStats` in the plan XML).
"Before" is the query shape EF generated before this change, on the original index:

| Query | Before: rows to app / logical reads / elapsed | After: rows / logical reads / elapsed | Plan change |
|-------|-----------------------------------------------|----------------------------------------|-------------|
| Wallet balance (overdraft check) | 100,000 / 288,087 / 162 ms | 1 / 963 / 10 ms | Index Seek + 100,000 Key Lookups (clustered seeks) -> covering Index Seek + Stream Aggregate |
| Wallet summary | 100,000 / 288,087 / 203 ms | 6 / 966 / 22 ms | same lookups for every column -> one covering seek + `TOP (5)` on the index order |
| Reconciliation, one day (measured on the ledger table before the Operations split) | 122,976 / 4,179 / 87 ms | 2,035 / 42 / 1 ms | Clustered Index Scan (date filtered in C#) -> range Index Seek on `CreatedAt` |

"Before" elapsed times are server-side only; the 100,000-row result sets also had to cross the
network and be materialised by EF, which the "after" versions avoid. The full table and the
`.sqlplan` files (open in SSMS / Azure Data Studio) are in [`docs/sql`](docs/sql/plan-results.md).
Reproduce with:

```powershell
$env:CLEARPAY_BENCH_SA_PASSWORD = '<sa password of a throw-away local container>'
./scripts/sql/Measure-LedgerPlans.ps1 -Server '127.0.0.1,14339'
```

<a id="en-observability"></a>

### Observability: One Payment, One Trace, Across Both Services

Both hosts call `AddClearPayObservability` (`src/ClearPay.ServiceDefaults`): **Serilog** writes
structured logs to the console and **Seq**, every event carrying `TraceId` / `SpanId` and `Service`;
**OpenTelemetry** exports traces and metrics over OTLP to a collector that fans out to **Tempo**
(Grafana), **Seq** (traces next to logs) and **Prometheus**. Nothing is exported unless
`Otel:Endpoint` / `Seq:Url` are set, so tests and a plain `dotnet run` need no collector.

How the trace survives the asynchronous hops:

1. ASP.NET Core and SqlClient instrumentation create the request and SQL spans; `ClearPay.Payments`
   adds one client span per PSP call (`psp simbank-rest authorize`), marked as an error with the
   decline code when the PSP declines or times out.
2. Every new outbox row stores the request's W3C `traceparent` (`OutboxMessage.TraceParent`, stamped in
   `SaveChanges`). The Hangfire outbox job runs up to a minute later with no ambient context; it starts
   `outbox <event type>` as a **child of the stored traceparent**, so the publish joins the original trace.
3. `RabbitEventPublisher` writes `traceparent` / `tracestate` into the AMQP headers; the consumer host
   in the Operations service starts its `process` span as a child of them; the inbox and projection
   SQL spans nest under it. Retries keep the headers, so a retried delivery shows up in the same trace
   with a higher `messaging.rabbitmq.attempt`.
4. Every HTTP response carries a `traceparent` header and every problem response a `traceId`, so
   support starts from what the client saw.

Measured locally on 2026-11-09 with the stack below and demo data (one 250.00 TRY card payment, a
40.00 refund, a payment declined by the PSP with code `51`, a 3-D Secure decline): the captured
payment's trace held **75 spans in 2 services** (request, both PSP calls, SQL, 6 outbox publishes,
RabbitMQ consumers and their SQL in `ClearPayOps`); the declined one 38 spans. Custom metrics
`clearpay_psp_calls_total{provider,operation,outcome}` and `clearpay_threeds_results_total{status}`
were in Prometheus.

The declined payment in Grafana (Tempo): the 402 request, the failed `psp simbank-rest authorize`
span, and - 39 s later - the outbox publishes and the Operations service's consumers in the same trace:

![Declined payment trace in Grafana Tempo](docs/assets/observability/grafana-tempo-trace-declined.png)

The same trace id in Seq: log events and spans of both services, including
`PSP Declined -> payment Declined` and the Operations service's `Notification card-payment-declined`:

![Declined payment logs and spans in Seq](docs/assets/observability/seq-logs-declined-payment.png)

The captured payment's full trace is in
[`docs/assets/observability/grafana-tempo-trace-captured.png`](docs/assets/observability/grafana-tempo-trace-captured.png).
How to use this when a payment fails: [**Runbook: tracing a failed card payment**](docs/RUNBOOK-failed-payment.md).

```bash
docker compose -f docker-compose.observability.yml up -d   # collector, Tempo, Prometheus, Grafana :3000, Seq :5341
# then start both hosts with
#   Otel__Endpoint=http://localhost:4317  Seq__Url=http://localhost:5341
```

All observability containers bind to 127.0.0.1 and run without authentication (Grafana anonymous
admin): local demo only.

<a id="en-api"></a>

### API Design

JSON API, JWT bearer auth, OpenAPI document at `/swagger`. Errors use RFC 7807
`application/problem+json`.

| Method | Path | Notes |
|--------|------|-------|
| POST | `/api/register` | Creates a customer, returns a JWT (201) |
| POST | `/api/token` | Email + password -> JWT (8 h) |
| POST | `/api/token/firebase` | Exchanges a Firebase ID token for a JWT (only if Firebase is configured) |
| POST | `/api/password/forgot`, `/api/password/reset` | Password recovery (the demo mailer writes to the log) |
| GET | `/api/wallet` | Balance (derived), month in/out, last movements |
| POST | `/api/transfers` | `Idempotency-Key` required. 201 / 400 / 401 / 403 frozen / 404 recipient / 409 replay / 422 insufficient funds / 503 retryable |
| GET | `/api/transfers/{id}` | Only the sender or recipient can read it; others get 404 |
| POST | `/api/topup`, `/api/withdraw` | `Idempotency-Key` required; 202 when the simulated gateway times out |
| GET | `/api/movements` | Paged (`page`, `pageSize` 1-50) |
| GET | `/api/receipts/{correlationId}` (`/pdf`) | Only parties to the movement |
| GET/POST | `/api/cards` | Linked cards store last 4 digits, scheme and label only (no PAN, no CVV) |
| POST | `/api/3ds/sessions` | `Idempotency-Key` required. Starts a simulated 3-D Secure authentication; 201 with `acsUrl`, 409 on replay |
| GET | `/api/3ds/sessions/{id}` | Session state; owner only (404 for others) |
| GET/POST | `/fake-acs?creq=...` | Fake issuer challenge page (anonymous, HTML) |
| POST | `/api/3ds/callback` | Signed challenge result (form post, anonymous). 200 / 400 stale / 401 bad signature / 409 / 410 timed out |
| GET | `/api/payments/providers` | Names of the registered PSP adapters |
| POST | `/api/payments` | `Idempotency-Key` required. `{ threeDsSessionId, provider?, capture? }`. 201 / 202 PSP timeout / 402 declined / 409 replay or 3DS already used / 422 3DS not authenticated |
| POST | `/api/payments/{id}/capture`, `/void` | `Idempotency-Key` required. 200 / 202 / 402 / 409 / 422 wrong state |
| POST | `/api/payments/{id}/refunds` | `Idempotency-Key` required. `{ amount }`, partial allowed. 200 / 202 / 402 declined (ledger reversed) / 409 / 422 exceeds refundable or insufficient wallet balance |
| GET | `/api/payments/{id}` | Payment with its operations; owner only |
| POST | `/api/admin/psp/settlement-files?date=yyyy-MM-dd` | `Admin` role: the fake PSPs drop that day's settlement files now (otherwise 01:00 UTC) |
| GET | Operations service `/api/reconciliation/runs`, `/runs/{id}` | `Admin` JWT from the ledger host: run summaries; one run with every matched / mismatched item |
| POST | Operations service `/api/reconciliation/runs?date=yyyy-MM-dd` | `Admin`: reconcile a business date now (default yesterday UTC) |
| GET | Operations service `/api/notifications?recipient=` | `Admin`: the demo mailbox |
| GET/POST | `/api/admin/*` | `Admin` role: freeze/unfreeze, outbox, audit search |
| GET | `/api/health` | Liveness plus Redis/RabbitMQ status |

Design decisions:

- **The debited wallet is never a request field.** The sender is always the `NameIdentifier`
  claim of the JWT; extra body fields such as `fromUserId` are ignored (covered by tests).
- **Strangers get 404, not 403**, so transfer and receipt ids cannot be probed.
- **Status codes carry meaning a client can act on**: 409 = already done, 422 = business rule,
  503 = safe to retry with the same key.
- **Two auth schemes, one user store**: cookie auth for Razor (with antiforgery), JWT for the
  API and mobile; the money API never accepts the browser cookie.

<a id="en-stack"></a>

### Technology Stack

| Area | Choice |
|------|--------|
| Runtime | .NET 8 (C# 12), ASP.NET Core Razor Pages + Web API in one host |
| Data | SQL Server 2022 (EF Core 8 migrations); SQLite for fast integration tests |
| Identity | ASP.NET Core Identity, JWT bearer, optional Google/Apple/Firebase sign-in |
| Validation | FluentValidation |
| Background jobs | Hangfire: ledger host (outbox, 3-D Secure expiry every minute, PSP settlement files 01:00); Operations service (reconciliation 02:30) |
| Cache / messaging | Redis (StackExchange.Redis), RabbitMQ (RabbitMQ.Client: topic exchange, publisher confirms, TTL retry queues, DLQ, inbox), both optional |
| Realtime | SignalR (refresh hints only, never balances) |
| Docs | Swashbuckle / OpenAPI |
| Mobile | Flutter (Dart 3), `http`, `flutter_secure_storage`, `signalr_netcore` |
| Observability | Serilog (Seq sink), OpenTelemetry traces + metrics (OTLP -> collector -> Tempo, Prometheus, Seq), Grafana |
| Tests | xUnit, FluentAssertions, `WebApplicationFactory`, Flutter widget tests |
| CI | GitHub Actions with a SQL Server service container |

<a id="en-structure"></a>

### Repository Structure

```text
clearpay-ledger/
├── src/
│   ├── ClearPay.Domain/            # Ledger entities and money rules, no packages
│   ├── ClearPay.Application/       # Ports, commands/outcomes, validators, reconciliation engine
│   ├── ClearPay.Infrastructure/    # EF Core, migrations, Identity/JWT, gateways, PSPs, outbox, cache
│   ├── ClearPay.Messaging/         # RabbitMQ topology, consumer host with retry/DLQ, inbox
│   ├── ClearPay.ServiceDefaults/   # Serilog + OpenTelemetry setup
│   ├── ClearPay.Operations/        # Separate service: notifications + reconciliation, own database
│   └── ClearPay.Web/               # Program.cs, API controllers, Razor Pages, SignalR hub
├── tests/
│   ├── ClearPay.Tests/             # xUnit unit, integration, architecture, SQL Server and RabbitMQ tests
│   └── ClearPay.Operations.Tests/  # Operations service and cross-service tests
├── mobile/clearpay/                # Flutter client (JWT), see its own README
├── docs/                           # Screenshots, runbook, SQL plans
├── scripts/sql/                    # Execution-plan measurement script
├── docker-compose.yml              # Local SQL Server, Redis, RabbitMQ
├── docker-compose.observability.yml # Collector, Tempo, Prometheus, Grafana, Seq
├── .env.example                    # Placeholder values for docker compose
└── .github/workflows/ci.yml        # .NET + Flutter CI
```

<a id="en-tests"></a>

### Tests & CI

#### Results (measured locally)

| Suite | Command | Infrastructure | Result |
|-------|---------|----------------|--------|
| .NET, full (2026-11-09) | `dotnet test` on `tests/ClearPay.Tests` and `tests/ClearPay.Operations.Tests` with `CLEARPAY_TEST_SQLSERVER` and `CLEARPAY_TEST_RABBITMQ` set | SQLite files (created by the tests) + SQL Server 2022 + RabbitMQ 3.13 in Docker | **281 passed, 0 failed, 0 skipped** (271 + 10) |
| .NET, fast (2026-11-09) | same commands, variables unset | SQLite only | 263 passed, 0 failed, 18 skipped (ledger 255 + 16 skipped; Operations 8 + 2 skipped) |
| Flutter (2026-11-03) | `flutter analyze` and `flutter test` in `mobile/clearpay` | none (mocked HTTP) | no analyzer issues; **20 passed, 0 failed** |
| NuGet vulnerabilities | `dotnet list <project> package --vulnerable --include-transitive` | none | no vulnerable packages |

Counts are test cases as reported by the runners (xUnit theories count once per data row).

What the money tests cover:

| Concern | Where |
|---------|-------|
| Debits = credits, balance derived from entries, every pair sums to zero | `LedgerPairTests`, `LedgerInvariantTests`, `LedgerSafetyApiTests.Ledger_stays_balanced...` |
| Idempotent replay returns 409 and posts nothing; same key with a different body | `TransferExecutorTests`, `TransferApiTests`, `LedgerSafetyApiTests.Errors_are_problem_details...` |
| Concurrent duplicate requests (same key, 8 in parallel) post exactly one transfer | `SqlServerLedgerTests` (SQL Server) |
| Concurrent different-key debits never overdraw the wallet | `SqlServerLedgerTests` (SQL Server) |
| Rollback when the database fails after rows were sent; key stays usable | `TransferRollbackTests` (SQLite), `SqlServerLedgerTests` (SQL Server) |
| Insufficient funds, frozen wallet, self transfer, unknown recipient | `TransferExecutorTests`, `TransferApiTests`, `LedgerSafetyApiTests` |
| Authentication: missing, forged and expired JWTs | `TransferApiTests`, `LedgerSafetyApiTests` |
| Authorization: body cannot choose the debited wallet; strangers cannot read transfers or receipts | `LedgerSafetyApiTests`, `TransferApiTests` |
| Validation: amount <= 0 or > 2 decimals, malformed JSON, oversized key/description | `LedgerSafetyApiTests`, `SqlServerLedgerTests` |
| Audit row and pending outbox row share the ledger correlation id; none on rejection | `LedgerSafetyApiTests` |
| 3-D Secure: state machine, signed CReq/CRes, forged / stale / replayed callbacks, expiry, full browser round trip | `ThreeDsServiceTests`, `ThreeDsDomainAndSignerTests`, `ThreeDsApiTests` |
| 3-D Secure: 8 conflicting callbacks in parallel give exactly one transition and one outbox event | `ThreeDsSqlServerTests` (SQL Server) |
| PSP adapters: all three authorize + capture; declines post nothing; void before capture; partial refunds capped; refund decline reversed; lost PSP response resumed without double posting; replayed keys | `CardPaymentServiceTests`, `CardPaymentApiTests` |
| Ten parallel 30.00 refunds on a 100.00 payment: exactly three succeed; CHECK constraint rejects over-refund | `CardPaymentSqlServerTests` (SQL Server) |
| Reconciliation matching rules and settlement CSV | `ReconciliationEngineTests` |
| Operations service: every mismatch kind from real ledger payments, events and PSP files without touching the ledger DB; missing file reported; nightly job targets yesterday; skip / force | `ReconciliationAcrossServicesTests` (Operations.Tests) |
| Operations service: notifications once per event, operator report, out-of-order projection, poison events, admin-only API with the ledger's JWT | `NotificationAndProjectionTests`, `OperationsApiTests` (Operations.Tests), `PspSimulatorAdminApiTests` |
| Two hosts over a real broker: ledger outbox -> RabbitMQ -> Operations projection + notification; full re-publish notifies once | `CrossServiceRabbitTests` (Operations.Tests, RabbitMQ) |
| Operations migration and `ops.usp_SettlementCandidates` on SQL Server; architecture: Operations never references the ledger | `OperationsSqlServerTests` (SQL Server), `ArchitectureTests` |
| Tracing: the outbox row keeps the request's traceparent and the outbox publish continues that trace; AMQP headers round-trip the context | `TracingTests` |
| Inbox: same message twice posts once; a failing handler rolls back its inbox row; poison payloads | `InboxProcessorTests` |
| RabbitMQ: outbox re-publish and broker redelivery after a crash before ack post once; retries wait 200 / 400 ms then succeed once; exhausted retries and poison messages land in the DLQ | `RabbitMessagingTests` (RabbitMQ) |
| Outbox: failed publish stays Pending until the attempt limit, batch stops, message id = row id | `OutboxProcessorTests`, `RabbitOutboxPublisherTests` |
| Stored procedures return the same balance / summary / settlement rows as the LINQ they replace, join the caller's transaction, and the covering indexes exist after migration | `LedgerProcedureSqlServerTests` (SQL Server) |
| Outbox worker, gateway timeout keeps the outbox row | `OutboxProcessorTests`, `FundingExecutorTests` |
| Database CHECK constraint rejects zero-amount rows | `SqlServerLedgerTests` |
| Architecture rules between layers | `ArchitectureTests` |

#### Run the tests

```bash
# Fast suite: SQLite only, no Docker needed (SQL Server and RabbitMQ tests are reported as skipped)
dotnet test tests/ClearPay.Tests/ClearPay.Tests.csproj -c Release
dotnet test tests/ClearPay.Operations.Tests/ClearPay.Operations.Tests.csproj -c Release

# Full suite: throw-away SQL Server and RabbitMQ containers
docker run -d --name clearpay-test-sql -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=<local-password>" \
  -p 127.0.0.1:14339:1433 mcr.microsoft.com/mssql/server:2022-latest
export CLEARPAY_TEST_SQLSERVER="Server=127.0.0.1,14339;User Id=sa;Password=<local-password>;TrustServerCertificate=True;Encrypt=True"
docker run -d --name clearpay-test-rabbit -p 127.0.0.1:25672:5672 rabbitmq:3.13-management-alpine
export CLEARPAY_TEST_RABBITMQ="amqp://guest:guest@127.0.0.1:25672/"
dotnet test tests/ClearPay.Tests/ClearPay.Tests.csproj -c Release
dotnet test tests/ClearPay.Operations.Tests/ClearPay.Operations.Tests.csproj -c Release

# Flutter
cd mobile/clearpay
cp firebase_options.template.dart lib/firebase_options.dart
flutter pub get && flutter analyze && flutter test
```

Each SQL Server test class creates its own database (`ClearPayTest_<random>`), applies the real
EF migrations and drops it afterwards.

#### CI

`.github/workflows/ci.yml` runs on pushes to `main` and on pull requests:

1. **.NET job** with `mcr.microsoft.com/mssql/server:2022-latest` and `rabbitmq:3.13-management-alpine` service containers: restore,
   build and test both test projects (Release) with `CLEARPAY_TEST_SQLSERVER` and `CLEARPAY_TEST_RABBITMQ` set (so the concurrency, rollback and messaging tests
   run against real servers), upload TRX results, then fail the job if
   `dotnet list package --vulnerable --include-transitive` reports anything.
2. **Flutter job**: copy the Firebase options template, `flutter pub get`, `flutter analyze`,
   `flutter test`.

<a id="en-install"></a>

### Installation

Prerequisites: .NET SDK 8 or newer, Docker (or SQL Server LocalDB on Windows), Flutter for the
mobile client.

```bash
# 1. Infrastructure (SQL Server, Redis, RabbitMQ)
cp .env.example .env            # set MSSQL_SA_PASSWORD to your own local value
docker compose up -d

# 2. Local secrets (never committed)
dotnet user-secrets set "ConnectionStrings:ClearPay" \
  "Server=localhost,1433;Database=ClearPay;User Id=sa;Password=<your MSSQL_SA_PASSWORD>;TrustServerCertificate=True;Encrypt=True" \
  --project src/ClearPay.Web
dotnet user-secrets set "Jwt:SigningKey" "<at least 32 random characters>" --project src/ClearPay.Web
dotnet user-secrets set "ThreeDs:AcsSharedSecret" "<at least 32 random characters>" --project src/ClearPay.Web   # optional in Development
dotnet user-secrets set "Seed:AdminPassword" "<password for the local admin account>" --project src/ClearPay.Web   # optional

# 3. Run (migrations are applied on startup)
dotnet run --project src/ClearPay.Web --launch-profile http
# Web UI: http://localhost:5153   Swagger: http://localhost:5153/swagger
```

Notes:

- In `Development`, `appsettings.Development.json` points at SQL Server LocalDB
  (`(localdb)\MSSQLLocalDB`); on Windows you can skip Docker and the connection-string secret.
- If `Jwt:SigningKey` is not set, Development falls back to a public placeholder key; every other
  environment refuses to start without a configured key.
- `Seed:AdminPassword` creates `admin@clearpay.test` with the `Admin` role in Development only. If
  it is not set, no admin account is created; register normally instead.
- Google, Apple and Firebase sign-in stay disabled until their settings are provided
  (`Authentication:Google:*`, `Authentication:Apple:*`, `Firebase:ProjectId`).
- Run the Operations service as well for notifications and reconciliation: see [Operations Service](#en-operations).
- Mobile client: see [`mobile/clearpay/README.md`](mobile/clearpay/README.md).

<a id="en-screenshots"></a>

### Demo Screenshots

| | |
|---|---|
| ![Wallet overview](docs/assets/shot-ozet.png) | ![Linked cards](docs/assets/shot-kartlar.png) |
| Wallet overview: balance derived from the ledger, last movements · Cüzdan özeti: defterden türetilen bakiye, son hareketler | Linked cards: only last four digits, scheme and label are stored; the number shown is the standard Visa test number · Kartlarım: yalnızca son dört hane, şema ve etiket saklanır; görünen numara standart Visa test numarasıdır |

Both screenshots show the local Development seed account on a `.test` domain with the Turkish UI; no real personal data.

Observability screenshots are in [Observability](#en-observability).

<a id="en-security"></a>

### Security Considerations

- **No secrets in the repository.** Connection strings, JWT signing key, OAuth and Firebase settings
  come from user-secrets or environment variables. `appsettings*.json` and `.env.example` contain
  placeholders only. Firebase client files are generated locally by `flutterfire configure` and
  are gitignored.
- **Authorization by construction.** The debited wallet comes from the token, not the request body.
  Transfers and receipts are scoped to their parties (404 for others). Admin endpoints require the
  `Admin` role.
- **JWT**: HMAC-SHA256, issuer, audience, signature and lifetime validated (1 minute clock skew);
  8-hour tokens without refresh tokens or server-side revocation.
- **Card data**: only last four digits, scheme and a label are stored; PAN and CVV are never persisted.
- **Data integrity** relies on database constraints as well as code: unique idempotency key,
  non-zero amount CHECK constraint, restrict-delete foreign keys.
- **Known gaps** for anything beyond a demo: no login lockout or rate limiting, Swagger is exposed
  in all environments, forwarded headers are trusted from any proxy, SignalR passes the JWT in the
  query string (standard for WebSockets, but it can appear in proxy logs), and there is no 2FA,
  KYC or AML.

<a id="en-limitations"></a>

### Limitations

- Portfolio demo. Not a licensed financial product; not audited; no real bank or card network
  integration (the gateway is simulated).
- Single currency (TRY), no FX, fees, interest, statements, limits or scheduled payments.
- Balance is still the sum of all of a wallet's entries on each check (now one covering index seek, about 1,000
  logical reads for a 100,000-row wallet - see the T-SQL section). A production ledger
  would add balance snapshots or a locked running balance per account.
- Serializable isolation keeps the overdraft check correct but converts contention into deadlock
  retries (503). Row-level locking of the sender's account would scale better.
- Concurrent duplicate top-up requests can both reach the simulated gateway before one of them
  wins the idempotency insert; the ledger still posts once. A real integration would reserve the
  key first and pass it to the provider as its idempotency key.
- Reconciliation buckets operations by the UTC date their PSP call started; an operation retried across
  midnight can land on different dates on each side and show up as two mismatches. Settlement files
  come from the simulators via a local folder, not from an SFTP drop; both services run on one machine in the demo.
- The outbox worker is a single Hangfire job with at-least-once delivery; it is not designed for
  multiple competing workers.
- Card refunds exist (`/api/payments/{id}/refunds`); wallet-to-wallet transfers cannot be refunded through the API or UI. There is no web UI for card payments yet (API and the fake ACS page only).
- The fast test suite uses SQLite; SQL Server-specific behaviour (isolation, deadlocks,
  constraints) is covered only by the SQL Server tests.
- Password-reset mail is only written to the log (`LogAccountMailer`); there is no real email delivery.
- Not deployed; runs locally.

<a id="en-license"></a>

### License

[MIT](LICENSE) (c) 2026 Halil Mert Develi.

Third-party components keep their own licences: vendored `anime.js` 3.2.2 (MIT) and the
`@microsoft/signalr` browser client (MIT) under `src/ClearPay.Web/wwwroot/js/vendor`; the Inter
font is loaded from Google Fonts (SIL Open Font License). NuGet and pub dependencies are restored
from their registries and are MIT, Apache-2.0 or BSD licensed, except Hangfire (LGPL-3.0, used
unmodified as a library).

<p align="right"><a href="#english">Back to top</a> · <a href="#turkce">Türkçe</a></p>

---

<a id="turkce"></a>

## Türkçe

> **Portföy demosudur, bankacılık ürünü değildir.** ClearPay lisanslı bir banka, ödeme kuruluşu veya elektronik para kuruluşu değildir. Gerçek para hareketi yoktur: yükleme ve çekim için kullanılan banka gateway'i ve kart ödemelerindeki ödeme sağlayıcıları (PSP) ile 3-D Secure banka sayfası simüle edilmiştir. Tutarlar demo Türk lirasıdır (TRY). Arayüz varsayılan olarak Türkçedir; İngilizce, Almanca ve Fransızca da mevcuttur.

- [Problem ve Çözüm](#tr-problem)
- [Özellikler](#tr-features)
- [Mimari](#tr-architecture)
- [Gösterilen Bankacılık Kavramları](#tr-concepts)
- [Çift Taraflı Defter](#tr-ledger)
- [Idempotency](#tr-idempotency)
- [Transaction Tutarlılığı](#tr-consistency)
- [Kart Ödemeleri: 3-D Secure (simülasyon)](#tr-3ds)
- [Kart Ödemeleri: PSP Adaptörleri, Tahsilat, İade, İptal](#tr-psp)
- [Operations Servisi: Bildirimler ve Mutabakat](#tr-operations)
- [Mesajlaşma: RabbitMQ Retry, Dead-Letter Queue, Inbox](#tr-messaging)
- [T-SQL: Stored Procedure'ler, Covering Index'ler, Ölçülmüş Planlar](#tr-tsql)
- [Gözlemlenebilirlik: Tek Ödeme, Tek Trace, İki Servis](#tr-observability)
- [API Tasarımı](#tr-api)
- [Teknoloji Yığını](#tr-stack)
- [Depo Yapısı](#tr-structure)
- [Testler ve CI](#tr-tests)
- [Kurulum](#tr-install)
- [Demo Ekran Görüntüleri](#tr-screenshots)
- [Güvenlik Notları](#tr-security)
- [Sınırlamalar](#tr-limitations)
- [Lisans](#tr-license)

<a id="tr-problem"></a>

### Problem ve Çözüm

**Problem.** Klasik cüzdan hatası `UPDATE Wallet SET Balance = Balance - @amount` ifadesidir. Geçmişi kaybeder, denetlenemez, mobil istemci zaman aşımından sonra isteği tekrarladığında iki kez para çeker ve süreç borç ile alacak arasında çökerse parayı yarı yolda bırakır.

**Çözüm.** ClearPay bakiyeyi hiçbir zaman saklamaz. Her hareket, işaretli iki defter satırından (bir borç, bir alacak; toplamları sıfır) oluşan bir çifttir ve transfer kaydı, idempotency anahtarı, denetim (audit) satırı ve outbox mesajıyla birlikte **tek bir SQL Server transaction'ı** içinde yazılır. Bakiye, cüzdanın defter satırlarının toplamıdır. Aynı `Idempotency-Key` ile tekrarlanan istek **409 Conflict** döner ve hiçbir kayıt oluşturmaz.

<a id="tr-features"></a>

### Özellikler

| Alan | Yetenek | Konum |
|---|---|---|
| Defter | Dengeli borç/alacak çiftleri, türetilmiş bakiye, eksi bakiye kontrolü, ters kayıt (iade) çifti | `src/ClearPay.Domain/Ledger/LedgerPair.cs` |
| Transferler | Serializable transaction: defter çifti + transfer + idempotency anahtarı + audit + outbox | `src/ClearPay.Infrastructure/Persistence/SqlTransferExecutor.cs` |
| Yükleme / çekim | Simüle banka gateway'i üzerinden hazine (takas) cüzdanına karşı çiftler | `SqlFundingExecutor.cs`, `src/ClearPay.Infrastructure/Banking/` |
| 3-D Secure | Sahte ACS (banka) sayfası, iki yönde HMAC imzalı mesajlar, açık durum makinesi | `/api/3ds`, `/fake-acs`, `ThreeDsSigner` |
| Kart ödemeleri | Tek PSP arayüzü arkasında üç sahte sağlayıcı; yetkilendirme, tahsilat, kısmi iade, iptal (idempotent) | `IPaymentServiceProvider`, `PspRouter`, `/api/payments` |
| Mutabakat | Gece çalışan Hangfire job'ı, PSP settlement dosyalarını ayrı Operations servisinde eşleştirir | `src/ClearPay.Operations/` |
| Idempotency | `Idempotency-Key` header'ı, primary key benzersizliği, SHA-256 istek parmak izi | `SqlIdempotencyStore.cs`, `src/ClearPay.Web/Controllers/TransfersController.cs` |
| Outbox ve mesajlaşma | Commit edilmiş outbox satırları publisher confirms ile RabbitMQ'ya; retry, DLQ ve inbox ile tekilleştirme | `SqlOutboxProcessor.cs`, `src/ClearPay.Messaging/` |
| T-SQL | Stored procedure'ler, covering index'ler, ölçülmüş execution plan'lar | `LedgerProcedures.sql`, `docs/sql/` |
| Gözlemlenebilirlik | Serilog + OpenTelemetry; tek ödemenin iki servisteki tek trace'i | `src/ClearPay.ServiceDefaults/`, `docs/RUNBOOK-failed-payment.md` |
| Dekontlar | Correlation id ile JSON ve üretilen PDF dekont | `ReceiptsController.cs`, `src/ClearPay.Infrastructure/Documents/SimplePdfReceiptRenderer.cs` |
| Kimlik doğrulama | ASP.NET Identity (Razor için cookie), API/mobil için JWT, isteğe bağlı Google/Apple/Firebase | `src/ClearPay.Infrastructure/Identity/` |
| Yönetim | Cüzdan dondurma/çözme, outbox görüntüleme ve yeniden kuyruğa alma, audit arama | `AdminApiController.cs`, `SqlAdminPanel.cs`, `Pages/Admin.cshtml` |
| Bağlı kartlar | Yalnızca son dört hane, şema ve etiket saklanır | `CardsController.cs`, `src/ClearPay.Application/Funding/CardBindingParser.cs` |
| Gerçek zamanlı | SignalR `/hubs/wallet` yalnızca yenileme sinyali gönderir, bakiye göndermez | `src/ClearPay.Web/Realtime/` |
| Önbellek | İsteğe bağlı Redis cüzdan özeti önbelleği, commit sonrası geçersiz kılınır | `src/ClearPay.Infrastructure/Caching/` |
| Web arayüzü | Razor Pages: özet, havale, kartlar, yükle/çek, hareketler, dekont, yönetim | `src/ClearPay.Web/Pages/` |
| Yerelleştirme | `tr` (varsayılan), `en`, `de`, `fr` kaynakları | `src/ClearPay.Web/Localization/`, `src/ClearPay.Web/Resources/Localization/` |
| Mobil | QR, PDF dekont paylaşımı ve SignalR yenilemesi olan Flutter JWT istemcisi | `mobile/clearpay/lib/` |

<a id="tr-architecture"></a>

### Mimari

Defter host'u, bağımlılıkları tersine çevrilmiş (onion / clean) bir yapıdadır. Kurallar `ArchitectureTests` ile zorunlu kılınır (örneğin Domain hiçbir pakete referans vermez; Web sayfaları defter hesabı yapmaz ve gateway örneği oluşturmaz). Bildirimler ve mutabakat, kendi veritabanı olan ayrı bir serviste çalışır (bkz. [Operations Servisi](#tr-operations)).

```mermaid
flowchart LR
    subgraph Clients
        WEB["Razor Pages UI<br/>(cookie auth)"]
        MOB["Flutter app<br/>(JWT)"]
    end

    subgraph Host["ClearPay.Web (ASP.NET Core 8, single host)"]
        API["JSON API controllers<br/>/api/transfers, /api/topup, /api/withdraw,<br/>/api/wallet, /api/movements, /api/receipts"]
        PAGES["Razor PageModels"]
        HUB["SignalR /hubs/wallet<br/>(refresh hint only)"]
        SWAG["Swagger /swagger"]
    end

    subgraph App["ClearPay.Application"]
        PORTS["Ports: ITransferExecutor, IFundingExecutor,<br/>IWalletReader, IIdempotencyStore,<br/>IBankGateway, IOutboxPublisher"]
    end

    subgraph Dom["ClearPay.Domain"]
        LEDGER["LedgerPair, LedgerEntry, Wallet,<br/>Transfer, IdempotencyRecord,<br/>AuditLog, OutboxMessage"]
    end

    subgraph Infra["ClearPay.Infrastructure"]
        EXEC["SqlTransferExecutor /<br/>SqlFundingExecutor (EF Core)"]
        GW["Rest/Soap BankGateway (simulated)"]
        OUTBOX["Hangfire job -> SqlOutboxProcessor"]
        CACHE["Redis wallet-summary cache"]
        IDENT["ASP.NET Identity + JWT issuer"]
    end

    SQL[("SQL Server<br/>ledger + Identity")]
    REDIS[("Redis (optional)")]
    MQ[("RabbitMQ (optional)")]

    WEB --> PAGES
    MOB --> API
    MOB -.-> HUB
    PAGES --> PORTS
    API --> PORTS
    PORTS --> LEDGER
    EXEC --> PORTS
    EXEC --> SQL
    EXEC --> GW
    OUTBOX --> SQL
    OUTBOX --> MQ
    CACHE --> REDIS
    IDENT --> SQL
```

<sub>Proje referanslarından ve `Program.cs` dosyasından çıkarılan defter host'unun düzeni ve çalışma zamanı bağımlılıkları.</sub>

| Proje | İçerir | İçeremez |
|---------|-------|---------------|
| `ClearPay.Domain` | Defter tipleri ve para kuralları (`LedgerPair.Create`, `EnsureBalanced`, `NetOf`, `WouldGoNegative`) | EF, HTTP, paketler |
| `ClearPay.Application` | Use-case sözleşmeleri (port'lar), komutlar/sonuçlar, FluentValidation | Connection string, EF |
| `ClearPay.Infrastructure` | EF Core (SQL Server, testler için SQLite), Identity, JWT, Redis, RabbitMQ, Hangfire, gateway'ler | Razor, CSS |
| `ClearPay.Web` | Composition root, controller'lar, Razor Pages, SignalR hub | Defter aritmetiği |
| `ClearPay.Messaging` | Ortak yapı taşı: RabbitMQ topolojisi, retry / DLQ'lu tüketici host'u, inbox, trace header'ları | Domain tipleri, tablolar |
| `ClearPay.ServiceDefaults` | İki host'un ortak kullandığı Serilog + OpenTelemetry kurulumu | İş kodu |
| `ClearPay.Operations` | Ayrı servis: bildirimler + gece mutabakatı, kendi veritabanı (şema `ops`) | Defter host'una veya veritabanına herhangi bir referans |
| `mobile/clearpay` | Flutter istemcisi (JWT); yerel bakiye tutmaz | Herhangi bir para mantığı |

Redis ve RabbitMQ isteğe bağlıdır: Redis yoksa özet SQL'den okunur. `ConnectionStrings:RabbitMq` boşsa outbox olayları yalnızca log'a yazar; broker yapılandırılmış ama kapalıysa olaylar outbox'ta bekler ve broker geri geldiğinde yayınlanır. Tek doğruluk kaynağı SQL Server'dır.

<a id="tr-concepts"></a>

### Gösterilen Bankacılık Kavramları

- **Çift taraflı kayıt**: her hareket, aynı `PairId` ve `CorrelationId` değerini paylaşan bir borç (`-tutar`) ve bir alacaktan (`+tutar`) oluşur.
- **Türetilmiş bakiye**: `Wallet` üzerinde `Balance` sütunu yoktur; bakiye, cüzdanın işaretli kayıtlarının toplamıdır.
- **Eksi bakiye yok**: defter neti sıfırın altına düşecekse borç reddedilir (kontrol transaction içinde yapılır).
- **Idempotency**: `Idempotency-Key` header'ı, benzersiz index, istek parmak izi, tekrarda 409.
- **Atomik kayıt**: defter çifti + transfer + idempotency anahtarı + audit + outbox birlikte commit edilir veya birlikte geri alınır.
- **Transactional outbox**: entegrasyon olayları aynı transaction içinde satır olarak yazılır; bir arka plan job'ı bunları daha sonra yayınlar.
- **Denetim izi**: her kayıt için kim, ne, ne zaman ve correlation id tutulur.
- **Hesap dondurma**: dondurulmuş cüzdanlar para gönderemez veya çekemez (403); gelen alacaklar yine de kaydedilir.
- **Takas hesabı**: yükleme ve çekimler bir hazine cüzdanına karşı çift olarak yazılır; böylece dışarıdan gelen para da defteri dengede tutar.
- **Gateway zaman aşımı**: zaman aşımına uğrayan yükleme defter satırı oluşturmaz, bekleyen bir outbox satırı yazar ve 202 ile yanıtlanır; aynı anahtarla tekrar denemek çift alacak oluşturmaz.
- **Ters kayıt modeli**: geçmiş hiçbir zaman düzenlenmez; kart iadeleri ters bir çift yazar (müşteri -> hazine), PSP'nin reddettiği iade çifti geri kaydeder (`/api/payments/{id}/refunds`). Cüzdandan cüzdana transferlerin hâlâ iade endpoint'i yoktur.
- **3-D Secure (simülasyon)**: üye işyeri -> gateway -> sahte banka ACS sayfası -> HMAC imzalı callback; açık durum makinesi (`Initiated`, `Challenge`, `Authenticated`, `Failed`, `TimedOut`).
- **Dekontlar**: her kayıt correlation id ile JSON veya üretilen PDF olarak alınabilir.

<a id="tr-ledger"></a>

### Çift Taraflı Defter

```text
Transfer of 25.00 from A to B, correlation C, pair P

LedgerEntry  WalletId  Amount   PairId  CorrelationId  Kind
-----------  --------  -------  ------  -------------  --------
#1           A         -25.00   P       C              Transfer
#2           B         +25.00   P       C              Transfer
                       ------
                         0.00   (every pair, and therefore the whole ledger, sums to zero)
```

- `LedgerPair.Create`, executor'ların kullandığı tek kayıt oluşturucusudur. Pozitif olmayan tutarları ve aynı cüzdanlar arası işlemi reddeder, sonuç üzerinde `EnsureBalanced` çağırır.
- Veritabanı kendi korumasını ekler: `CK_LedgerEntry_Amount_NotZero`, `decimal(18,2)` tutarlar, silmeyi kısıtlayan (restrict) foreign key'ler ve `(WalletId, CreatedAt) INCLUDE (Amount, Kind, CorrelationId)` üzerinde bir covering index.
- Bakiye = `SUM(Amount) WHERE WalletId = @wallet`. Cüzdan özetleri Redis'te önbelleğe alınabilir; ancak her commit sonrasında geçersiz kılınır ve hiçbir zaman SQL'e geri yazılmaz.

Neden bakiye sütunu yok? Saklanan bakiye, kayıtlardan sapabilecek ikinci bir doğruluk kaynağıdır. Bakiyeyi türetmek "bakiye, kayıtların toplamıdır" kuralını yapısal olarak garanti eder; bedeli cüzdan satırları üzerinde bir okumadır ve demo ölçeğinde kabul edilebilirdir (bkz. [Sınırlamalar](#tr-limitations)).

<a id="tr-idempotency"></a>

### Idempotency

`POST /api/transfers` bir `Idempotency-Key` header'ı gerektirir (en fazla 128 karakter).

1. Transaction açılmadan önce anahtar aranır. Bulunursa orijinal `transferId` ve `correlationId` ile **409** döner; hiçbir şey yazılmaz.
2. Transaction içinde anahtar tekrar aranır, ardından defter çiftiyle birlikte eklenir. Anahtar `IdempotencyRecord` tablosunun primary key'idir; bu nedenle yarışan iki istek birden commit edemez: kaybeden taraf unique violation alır ve 409 ile yanıtlanır.
3. Kayıt; alıcı, tutar ve açıklamanın SHA-256 parmak izini saklar. Aynı anahtarın farklı bir gövdeyle kullanılması `KeyPayloadMismatch` (yine 409) olarak bildirilir ve asla borç oluşturmaz.

Neden "önbellekteki yanıtla 200" yerine 409? İkisi de savunulabilir. 409, tekrarı istemciye açıkça bildirir ("zaten işlendi, tekrar ücret alınmadı") ve orijinal id'leri yine döndürür; böylece istemci mevcut dekontu açabilir. Yükleme ve çekim de kendi anahtar kapsamlarıyla aynı kuralları izler.

```mermaid
sequenceDiagram
    participant C as Client
    participant API as TransfersController
    participant EX as SqlTransferExecutor
    participant DB as SQL Server
    C->>API: POST /api/transfers with Idempotency-Key K
    API->>EX: TransferCommand, sender taken from JWT
    EX->>DB: look up key K
    alt key already stored
        EX-->>API: Replay
        API-->>C: 409 with original transferId
    else new key
        EX->>DB: BEGIN TRAN (Serializable)
        EX->>DB: re-check K and overdraft
        EX->>DB: insert ledger pair, transfer, key, audit, outbox
        EX->>DB: COMMIT
        EX-->>API: Posted
        API-->>C: 201 with transferId and correlationId
    end
```

<sub>`SqlTransferExecutor` akışına göre idempotency anahtarlı transfer isteği.</sub>

<a id="tr-consistency"></a>

### Transaction Tutarlılığı

- Transfer ve yükleme/çekim işlemleri **Serializable** izolasyon seviyesinde tek bir EF Core transaction'ında çalışır. Eksi bakiye kontrolü gönderenin kayıtlarını bu transaction içinde okur; bu yüzden eşzamanlı iki borç eski bakiyeyi birlikte göremez.
- Çekişme altında SQL Server bir deadlock kurbanı seçebilir. Bu durum **503 "hiçbir ücret alınmadı, tekrar deneyin"** olarak döner; istemci aynı anahtarla tekrar dener ve ya başarılı olur ya da 409 alır.
- Commit öncesindeki her hata defter çiftini, transfer satırını, idempotency anahtarını, audit satırını ve outbox satırını birlikte geri alır; bu nedenle başarısız deneme anahtarı tüketmez.
- Dış yan etkiler asla transaction içinde gerçekleşmez. Önbellek geçersiz kılma ve SignalR bildirimleri commit sonrasında çalışır; RabbitMQ yayını daha sonra outbox üzerinden yapılır. Yüklemelerde simüle banka çağrısı defter transaction'ından **önce** yapılır ve yalnızca başarılı yanıt kaydedilir.
- Outbox worker'ı (Hangfire, dakikada bir) `Pending` satırları publisher confirms ile yayınlar; başarısız yayın satırı `Pending` bırakır (deneme sayacı artar), 5 başarısız denemeden sonra satır `Failed` olur ve admin API'sinden yeniden kuyruğa alınabilir. Teslimat en az bir kez (at-least-once) garantilidir; tüketiciler `MessageId` üzerinden inbox tablosuyla tekilleştirme yapar (bkz. [Mesajlaşma](#tr-messaging)).

<a id="tr-3ds"></a>

### Kart Ödemeleri: 3-D Secure (simülasyon)

Kart ödemesinde para hareketinden önce kimlik doğrulama yapılır. Buradaki her şey simülasyondur: "banka", uygulamanın içinde çalışan **SimBank** adlı bir sayfadır; hiçbir kart ağına veya bankaya bağlanılmaz ve (test) kart numarasının yalnızca son dört hanesi saklanır.

```mermaid
sequenceDiagram
    participant M as Merchant (app / API client)
    participant G as Gateway (/api/3ds)
    participant A as Fake ACS (/fake-acs, "SimBank")
    M->>G: POST /api/3ds/sessions (JWT, Idempotency-Key, amount, test PAN)
    G->>G: Luhn check, keep last 4, status Initiated -> Challenge
    G-->>M: 201 { sessionId, status, acsUrl = /fake-acs?creq=<signed> }
    M->>A: open acsUrl in a browser / web view
    A->>A: verify CReq HMAC, show amount + card, ask OTP (demo: 123456)
    A-->>M: auto-submitting form with the signed CRes
    M->>G: POST /api/3ds/callback (form: sessionId, transStatus, eci, authValue, issuedAt, nonce, signature)
    G->>G: verify HMAC + time window, compare-and-set Challenge -> Authenticated | Failed | TimedOut
    G-->>M: 200 { status }
```

| Durum | Ne zaman |
|-------|----------|
| `Initiated` | Oturum satırı oluşturuldu (uzun süre görünmez; sahte directory server hemen karar verir) |
| `Challenge` | Kart kayıtlı: kart sahibi ACS doğrulamasını yanıtlamalı |
| `Authenticated` | ACS, doğrulama değeriyle `transStatus=Y` döndü (ECI 05) |
| `Failed` | Yanlış OTP, kart sahibi iptal etti veya `...0002` test kartı (banka doğrulama sormadan reddeder) |
| `TimedOut` | Süre dolmadan geçerli sonuç gelmedi (varsayılan 10 dakika); dakikada bir çalışan `clearpay-3ds-expiry` Hangfire job'ı veya okuma/callback sırasında işaretlenir |

Callback'e nasıl güvenilir:

- **İki yönde imzalı.** Gateway doğrulama isteğini (CReq), ACS de sonucu (CRes) her alan üzerinden HMAC-SHA256 ile imzalar (`ThreeDsSigner`). İki mesaj farklı önek kullanır; biri diğerinin yerine tekrar oynatılamaz. Karşılaştırma sabit zamanlıdır. Sahte veya değiştirilmiş sonuç **401** alır ve hiçbir şeyi değiştirmez.
- **Tazelik.** `issuedAt` sunucu saatine göre 5 dakika içinde olmalıdır (aksi halde **400**).
- **Tam olarak tek geçiş.** Durum değişikliği `UPDATE ... WHERE Id = @id AND Status = @expected` ile, audit satırı ve outbox olayıyla (`clearpay.3ds.authenticated`, `.failed`, `.timedout`) birlikte commit edilir. Aynı sonucun tarayıcıdan iki kez gönderilmesi 200 `AlreadyApplied` döner; bitmiş bir oturuma farklı sonuç **409**; süre dolduktan sonra gelen sonuç **410** döner ve oturum `TimedOut` olur. SQL Server testi sekiz çelişen callback'i paralel gönderir ve tam olarak bir geçiş ve bir outbox olayı olduğunu doğrular.
- **Callback bilerek anonimdir.** Banka sayfasından tarayıcı tarafından gönderilir; JWT veya cookie taşımaz. Güven yalnızca imzadan ve durum makinesinden gelir.
- **Anahtar.** `ThreeDs:AcsSharedSecret` (en az 32 bayt). Development herkese açık bir yer tutucuya düşer; diğer tüm ortamlarda anahtar yapılandırılana kadar 3-D Secure endpoint'leri hata verir (500).

Kimlik doğrulama tek başına deftere hiçbir şey yazmaz; ödemenin tahsil edilmesi ayrı bir adımdır.

<a id="tr-psp"></a>

### Kart Ödemeleri: PSP Adaptörleri, Tahsilat, İade, İptal

`Authenticated` durumdaki bir 3-D Secure oturumu tam olarak bir kart ödemesi için kullanılabilir ve bu ödeme müşterinin cüzdanına para yükler. Ödeme servisi sağlayıcılarla yalnızca `IPaymentServiceProvider` (authorize, capture, refund, void) üzerinden konuşur. Arkasında her biri farklı bir veri formatı kullanan üç sahte sağlayıcı vardır; böylece adaptör katmanı gerçek dönüştürme işi yapar:

| Adaptör | Veri formatı (uygulama içinde simüle) | Belgelenmiş hata tetikleyicisi |
|---------|---------------------------------------|--------------------------------|
| `simbank-rest` (varsayılan) | JSON, ondalık metin, `APPROVED` / `DECLINED` | yetkilendirme `x.51` -> red `51`; iade `x.52` -> red `R52` |
| `simbank-soap` | SOAP 1.1 XML zarfı, `ResponseCode` `00` = onay | yetkilendirme `x.05` -> red `05` |
| `simpay` | kuruş cinsinden tamsayılar, `succeeded` / `failed` | `x.99` ile biten her tutar -> PSP işlemi yapar ama **yanıt kaybolur** (çağıran zaman aşımı görür) |

Hiçbiri ağa bağlanmaz. Kendi "banka tarafı" kayıtları `sim.PspTransaction` tablosudur ve yalnızca simülatörler tarafından ayrı bir DbContext kapsamıyla yazılır; mutabakat için settlement dosyaları defterden değil buradan üretilir. Yeni bir sağlayıcı eklemek tek bir `IPaymentServiceProvider` kaydı demektir (`PspRouter` onu adıyla seçer).

| İşlem | Deftere etkisi | Kural |
|-------|----------------|-------|
| Authorize (yetkilendirme) | yok | 3-D Secure oturumu `Authenticated`, çağırana ait ve kullanılmamış olmalı (compare-and-set ile alınır, benzersiz index `UX_CardPayment_ThreeDsSessionId`) |
| Capture (tahsilat, oluşturmada varsayılan) | hazine -> müşteri, tür `CardPayment` | yalnızca PSP onayından sonra yazılır; ödeme başına bir kez |
| Refund (tam veya kısmi iade) | müşteri -> hazine, tür `Refund` | iadelerin toplamı <= tahsil edilen tutar; cüzdanda tutar hâlâ bulunmalı; PSP reddederse ters çift yazılır |
| Void (iptal) | yok | yalnızca tahsilattan önce |

Bir işlemin uçtan uca idempotent olması:

1. **SQL'de rezervasyon** (Serializable): `Idempotency-Key` + istek parmak izi, **deterministik sağlayıcı anahtarı** (`<paymentId>-auth`, `-capture`, `<operationId>-refund`) olan bir `PaymentOperation` satırı ve para kuralı (iadelerde cüzdan borcu burada yazılır; böylece yarışan iki iade kontrolü birlikte geçemez).
2. **PSP çağrısı her transaction'ın dışında** bu sağlayıcı anahtarıyla yapılır. Her sahte PSP anahtarı saklar ve tekrara yeniden işlem yapmak yerine ilk sonucu döner.
3. **Yanıt tek transaction'da kaydedilir**: işlem durumu, ödeme durumu, defter çifti, audit satırı ve outbox olayı (`clearpay.payment.authorized`, `.captured`, `.declined`, `.refunded`, `.refund_failed`, `.voided`).

PSP zaman aşımı işlemi `Pending` bırakır ve **202** döner. Aynı `Idempotency-Key` ile tekrar denemek aynı sağlayıcı anahtarıyla işlemi tamamlar: `simpay` kayıp yanıt testi yetkilendirme ve tahsilatı bitirmek için üç çağrı yapar ve yine tek bir PSP yetkilendirmesi, tek bir PSP tahsilatı ve tek bir defter çiftiyle biter. Kurallar veritabanında CHECK kısıtlarıyla da korunur (`CK_CardPayment_Refund_LE_Captured`, `CK_CardPayment_Captured_LE_Amount`).

<a id="tr-operations"></a>

### Operations Servisi: Bildirimler ve Mutabakat (ayrı süreç)

Bildirimler ve gece mutabakatı, **kendi veritabanı** (`ClearPayOps`, şema `ops`) olan ayrı bir ASP.NET Core servisinde, `src/ClearPay.Operations` içinde çalışır. Bu servis defter veritabanını hiçbir zaman açmaz (`ClearPay.Infrastructure`'a referans verirse veya `ClearPayDbContext`'ten bahsederse bir mimari testi build'i bozar); bildiği her şey RabbitMQ'dan veya PSP dosya klasöründen gelir. Bu adımdan önce defter host'unda hiç bildirim yoktu (yalnızca SignalR yenileme sinyalleri); bu yüzden bildirimler burada taşınmış değil yeni eklenmiştir. Mutabakat ise defter host'undan buraya taşınmıştır.

```mermaid
flowchart LR
    subgraph Ledger["ClearPay.Web (ledger host, DB: ClearPay)"]
        PAY["Card payments + fake PSPs"] --> OUT[("Outbox")]
        PSP["PSP end-of-day job 01:00 UTC"]
    end
    OUT -->|"publisher confirms"| EX{{"clearpay.events (topic)"}}
    PSP -->|"{provider}_{yyyyMMdd}.csv"| DROP[/"settlement drop folder"/]
    EX -->|"clearpay.payment.operation.recorded"| PROJ["projection consumer"]
    EX -->|"payment.captured / refunded / declined, 3ds.failed / timedout,<br/>reconciliation.completed"| NOTE["notification consumer"]
    subgraph Ops["ClearPay.Operations (DB: ClearPayOps, schema ops)"]
        PROJ --> P[("ops.PaymentOperation")]
        NOTE --> N[("ops.Notification")]
        JOB["Hangfire 02:30 UTC reconciliation"] --> P
        JOB --> R[("ops.ReconciliationRun / Item")]
    end
    DROP --> JOB
    JOB -->|"clearpay.reconciliation.completed"| EX
```

**Sınırdan ne geçer.** Defter, bir PSP işlemi oluşturulduğunda (`Pending`) veya yanıtlandığında (`Succeeded` / `Failed`) işlem satırıyla aynı transaction'da bir `clearpay.payment.operation.recorded` outbox olayı yazar. Operations servisi her işlemin son durumunu `ops.PaymentOperation` içinde tutar (inbox ile tekilleştirilir; geç gelen bir `Pending` görüntüsü son durumu asla ezmez). Simüle PSP'ler, sağlayıcı ve gün başına bir settlement CSV dosyasını `Reconciliation:SettlementDirectory` klasörüne bırakır (önce `.part` dosyasına yazılıp yeniden adlandırılır; okuyan taraf yarım dosya görmez). Bu, SFTP veya bucket teslimatının yerini tutar. İki servis yalnızca JWT ayarlarını (admin raporları) ve `src/ClearPay.Messaging` mesajlaşma yapı taşını (topoloji, tüketici host'u, inbox; domain tipi ve tablo yok) paylaşır.

**Bildirimler.** Bir tüketici `payment.captured`, `refunded`, `refund_failed`, `declined`, `3ds.failed`, `3ds.timedout` ve `reconciliation.completed` olaylarını `ops.Notification` satırlarına dönüştürür (demo "posta kutusu"; `LogNotificationSender` yalnızca log yazar, e-posta, SMS veya push sağlayıcısı çağrılmaz). Inbox ve benzersiz `SourceMessageId` sayesinde yeniden teslim edilen bir olay asla iki kez bildirim üretmez; servisler arası RabbitMQ testi defter outbox'ını baştan yeniden yayınlar ve yine tek bildirim görür.

**Mutabakat** (her gece 02:30 UTC, Hangfire job'ı `clearpay-ops-reconciliation-nightly`, **bir önceki gün** için; admin, Operations servisinde `POST /api/reconciliation/runs?date=yyyy-MM-dd` ile herhangi bir tarihi çalıştırabilir):

1. Klasördeki **settlement dosyaları** (`SettlementCsv`; bozuk satırlar raporlanır, eksik dosya çalıştırma kaydına yazılır). Job'ın bulacağı bir şey olsun diye iki belgelenmiş hata vardır: `.77` ile biten tutarlar PSP dosyasına yazılmaz, `.78` ile biten tutarlar bir kuruş fazla settle edilir.
2. **Bizim taraf**: projeksiyondaki o tarihe ait, başarısız olmamış Capture/Refund işlemleri (SQL Server'da `ops.usp_SettlementCandidates`).
3. **Eşleştirme** (`ClearPay.Application` içindeki `ReconciliationEngine`, saf ve birim testli): önce sağlayıcı referansıyla, ardından PSP yanıtı kaybolduğu için referansı olmayan işlemler için tür + üye işyeri referansıyla.

| Kalem türü | Anlamı | Operatör ne yapar |
|------------|--------|-------------------|
| `Matched` | aynı referans ve tutar | hiçbir şey |
| `MissingInSettlement` | biz tahsil ettik/iade ettik, PSP settle etmedi | PSP'ye iletir |
| `MissingInLedger` | PSP, kaydımız olmayan bir işlemi settle etti | araştırır; olası mükerrer veya yabancı işlem |
| `AmountMismatch` | aynı referans, farklı tutar | PSP'ye iletir |
| `PendingConfirmedBySettlement` | işlemimiz hâlâ `Pending` (zaman aşımı) ama PSP settle etmiş | tamamlar: aynı anahtarla tekrar dener |
| `PendingNotInSettlement` | hâlâ `Pending` ve settle edilmemiş | büyük olasılıkla PSP'de hiç gerçekleşmedi |

Her kalem saklanır, her uyuşmazlık sağlayıcı, referans ve tutarlarla birlikte uyarı olarak log'lanır ve çalıştırma `clearpay.reconciliation.completed` olayını yayınlar; bildirim tüketicisi bunu `operations-team` için bir rapora dönüştürür. Bu yayın commit sonrasında en iyi çabayla yapılır (doğruluk kaynağı saklanan çalıştırmadır; servisin kendi outbox'ı yoktur). Tamamlanmış çalıştırması olan bir tarih gece job'ı tarafından atlanır (Hangfire başarısız çalıştırmayı üç kez yeniden dener); elle başlatılan çalıştırma her zaman yeni bir çalıştırma oluşturur.

Defter host'unun yanında yerelde çalıştırma:

```bash
dotnet ef database update --project src/ClearPay.Operations --connection "<ClearPayOps connection string>"   # veya başlangıçta migration'ı kendisi uygular
dotnet run --project src/ClearPay.Operations --launch-profile http    # http://localhost:5163/health
```

İki serviste de `ConnectionStrings:Operations`, `ConnectionStrings:RabbitMq`, defter host'uyla aynı `Jwt:*` değerleri ve aynı `Reconciliation:SettlementDirectory` ayarlanmalıdır (varsayılan: işletim sisteminin temp klasörü altında `clearpay-settlements`).

<a id="tr-messaging"></a>

### Mesajlaşma: RabbitMQ Retry, Dead-Letter Queue, Inbox

Outbox satırları `clearpay.events` topic exchange'ine yayınlanır (routing key = olay türü, `MessageId` = outbox satır id'si, kalıcı, **publisher confirms**). Broker yapılandırılmış ama erişilemiyorsa veya onay vermiyorsa publisher hata fırlatır: satır `Attempts + 1` ile `Pending` kalır ve toplu işlem durur; 5 başarısız denemeden sonra satır `Failed` olur ve admin yeniden kuyruğa alabilir. Mesajlaşmayı yalnızca boş bir `ConnectionStrings:RabbitMq` kapatır (satırlar bu durumda log'a yazılır).

Her tüketicinin kendi kuyruk seti vardır ve `RabbitTopology` tarafından idempotent şekilde tanımlanır:

```text
clearpay.events (topic) --clearpay.payment.captured--> clearpay.cashback ----> handler
                                                          |        ^
                       exception, attempt n < max  -------+        | TTL expires, dead-letters back
                                                          v        |
                                     clearpay.cashback.retry.1 (1 s) / .retry.2 (5 s) / .retry.3 (25 s)
                       attempts exhausted, or poison (reject) ---> clearpay.cashback.dlq  (x-last-error, x-death)
```

- **Backoff ile retry**: hata olursa tüketici mesajın `x-attempt` + 1 içeren bir kopyasını sonraki gecikme kuyruğuna yayınlar ve orijinali onaylar (ack); TTL dolunca broker mesajı geri yönlendirir. Varsayılanlar: 4 deneme, 1 sn / 5 sn / 25 sn gecikme (`Messaging:MaxAttempts`, `Messaging:RetryDelaysMs`).
- **Dead-letter queue**: son denemeden sonra kopya, son hata `x-last-error` içinde olacak şekilde `<queue>.dlq` kuyruğuna gider; hiçbir zaman başarılı olamayacak bir mesaj (`PoisonMessageException`, eksik `MessageId`) yeniden kuyruğa alınmadan reddedilir ve broker onu hemen oraya yönlendirir.
- **Tüketici idempotency'si (inbox)**: `InboxProcessor`, `InboxMessage (Consumer, MessageId)` satırını ve handler'ın değişikliklerini **tek transaction'da** ekler, ardından mesaj onaylanır. Yeniden teslim (outbox'ın tekrar yayını, commit ile ack arasında çökme sonrası broker teslimi veya retry) satırı bulur ve handler çalıştırılmadan onaylanır; yarışan iki teslim primary key'de çakışır ve kaybeden yan etkilerini geri alır.
- **Bunu kanıtlayan, para yazan tüketici**: `CashbackOnCaptureHandler` (demo kampanya), tahsil edilen her kart ödemesinin %1'ini `clearpay-promotions` kesesinden müşteriye yatırır (defter türü `Cashback`). RabbitMQ testleri aynı mesajı iki kez yayınlar, tüketiciyi commit'ten sonra ama ack'ten önce çökertir, handler'ı başarılı olmadan önce iki kez başarısız yapar ve sonsuza dek başarısız yapar: cashback tam olarak bir kez yazılır (dead-letter'a giden mesaj için hiç yazılmaz).
- **Bağlantı**: otomatik kurtarmalı, geç açılan tek uzun ömürlü bağlantı; böylece uygulama broker olmadan da başlar. `/api/health` anlık olarak `up`, `down` veya `off` bildirir.
- **Trace bağlamı**: `traceparent` / `tracestate` AMQP header'larında taşınır ve tüketici kendi span'ını yayınlayanın span'ının çocuğu olarak başlatır (`ClearPay.Messaging` activity source).

<a id="tr-tsql"></a>

### T-SQL: Stored Procedure'ler, Covering Index'ler, Ölçülmüş Planlar

Sık kullanılan okuma yolları eskiden bir cüzdanın tüm defter satırlarını uygulamaya çekip C# içinde toplayan EF sorgularıydı. SQL Server'da artık covering index'lerden yanıt veren stored procedure'ler çağrılır (hızlı testlerdeki SQLite eşdeğer LINQ'i korur; `LedgerSql` hangisinin kullanılacağını seçer).

| Procedure | Kullanan | Aradığı index |
|-----------|----------|---------------|
| `dbo.usp_LedgerWalletBalance @WalletId` | transfer, çekim ve kart iadesindeki eksi bakiye kontrolü (çağıranın Serializable transaction'ı **içinde** çalışır) | `IX_LedgerEntry_WalletId_CreatedAt (WalletId, CreatedAt) INCLUDE (Amount, Kind, CorrelationId)` |
| `dbo.usp_LedgerWalletSummary @WalletId, @MonthStart` | `/api/wallet` ve ana sayfa: bakiye, aylık giriş/çıkış, son 5 hareket tek seferde | aynı index (aggregate + geriye doğru `TOP (5)` aralık taraması, sıralama yok) |
| `dbo.usp_PaymentSettlementCandidates @From, @To` (bir sonraki adıma kadar) | gece mutabakatı (bir günün tahsilat / iadeleri). Mutabakat Operations servisine taşınınca defterden kaldırıldı; servis kendi projeksiyonunda aynı yapıyı `ops.usp_SettlementCandidates` olarak kullanır | `IX_PaymentOperation_CreatedAt_Type (CreatedAt, Type) INCLUDE (...)`, artık `ops.PaymentOperation` üzerinde |

Procedure'ler `src/ClearPay.Infrastructure/Persistence/Sql/LedgerProcedures.sql` içindedir ve `AddLedgerProceduresAndCoveringIndexes` EF migration'ı ile oluşturulur. Daha dar bir covering index, Serializable eksi bakiye kontrolünün aldığı key-range kilitlerini de daraltır.

**Gerçek execution plan'lardan önce / sonra** (`scripts/sql/Measure-LedgerPlans.ps1`: sıfır veritabanı, tüm migration'lar, sentetik veri: ölçülen cüzdanda 100.000 olmak üzere 1.000.000 defter satırı ve 125.000 ödeme işlemi; Docker'da SQL Server 2022; ısınmış önbellek; 5 çalıştırmanın medyanı; okuma / CPU / süre değerleri plan XML'indeki `RunTimeCountersPerThread` ve `QueryTimeStats` alanlarından). "Önce", bu değişiklikten önce EF'in ürettiği sorgunun orijinal index üzerindeki halidir:

| Sorgu | Önce: uygulamaya giden satır / logical read / süre | Sonra: satır / logical read / süre | Plan değişikliği |
|-------|---------------------------------------------------|-------------------------------------|------------------|
| Cüzdan bakiyesi (eksi bakiye kontrolü) | 100.000 / 288.087 / 162 ms | 1 / 963 / 10 ms | Index Seek + 100.000 Key Lookup (clustered seek) -> covering Index Seek + Stream Aggregate |
| Cüzdan özeti | 100.000 / 288.087 / 203 ms | 6 / 966 / 22 ms | her sütun için aynı lookup'lar -> tek covering seek + index sırasına göre `TOP (5)` |
| Mutabakat, bir gün (Operations ayrımından önce defter tablosunda ölçüldü) | 122.976 / 4.179 / 87 ms | 2.035 / 42 / 1 ms | Clustered Index Scan (tarih C#'ta filtreleniyordu) -> `CreatedAt` üzerinde aralık Index Seek |

"Önce" süreleri yalnızca sunucu tarafıdır; 100.000 satırlık sonuç kümelerinin ayrıca ağdan geçmesi ve EF tarafından nesneye dönüştürülmesi gerekiyordu, "sonra" sürümleri bunu yapmaz. Tablonun tamamı ve `.sqlplan` dosyaları (SSMS / Azure Data Studio ile açılır) [`docs/sql`](docs/sql/plan-results.md) altındadır. Tekrar üretmek için:

```powershell
$env:CLEARPAY_BENCH_SA_PASSWORD = '<tek kullanımlık yerel container'ın sa şifresi>'
./scripts/sql/Measure-LedgerPlans.ps1 -Server '127.0.0.1,14339'
```

<a id="tr-observability"></a>

### Gözlemlenebilirlik: Tek Ödeme, Tek Trace, İki Servis

İki host da `AddClearPayObservability` (`src/ClearPay.ServiceDefaults`) çağırır: **Serilog** yapılandırılmış log'ları konsola ve **Seq**'e yazar, her olay `TraceId` / `SpanId` ve `Service` taşır; **OpenTelemetry** trace ve metrikleri OTLP ile bir collector'a gönderir, collector bunları **Tempo** (Grafana), **Seq** (trace'ler log'ların yanında) ve **Prometheus**'a dağıtır. `Otel:Endpoint` / `Seq:Url` ayarlanmadıkça hiçbir şey dışarı gönderilmez; bu yüzden testler ve düz bir `dotnet run` collector gerektirmez.

Trace asenkron adımlardan nasıl geçer:

1. ASP.NET Core ve SqlClient enstrümantasyonu istek ve SQL span'larını oluşturur; `ClearPay.Payments` her PSP çağrısı için bir client span ekler (`psp simbank-rest authorize`) ve PSP reddettiğinde veya zaman aşımına uğradığında bunu red koduyla hata olarak işaretler.
2. Her yeni outbox satırı isteğin W3C `traceparent` değerini saklar (`OutboxMessage.TraceParent`, `SaveChanges` içinde yazılır). Hangfire outbox job'ı bir dakikaya kadar sonra, ortam bağlamı olmadan çalışır; `outbox <event type>` span'ını **saklanan traceparent'ın çocuğu** olarak başlatır, böylece yayın orijinal trace'e katılır.
3. `RabbitEventPublisher`, `traceparent` / `tracestate` değerlerini AMQP header'larına yazar; Operations servisindeki tüketici host'u `process` span'ını bunların çocuğu olarak başlatır; inbox ve projeksiyon SQL span'ları onun altına yerleşir. Retry'lar header'ları korur; yeniden denenen teslim aynı trace'te daha yüksek bir `messaging.rabbitmq.attempt` ile görünür.
4. Her HTTP yanıtı bir `traceparent` header'ı, her hata yanıtı bir `traceId` taşır; destek ekibi istemcinin gördüğü yerden başlar.

2026-11-09'da yerelde, aşağıdaki yığın ve demo verisiyle ölçüldü (250,00 TRY'lik bir kart ödemesi, 40,00'lık bir iade, PSP'nin `51` koduyla reddettiği bir ödeme, bir 3-D Secure reddi): tahsil edilen ödemenin trace'i **2 serviste 75 span** içeriyordu (istek, iki PSP çağrısı, SQL, 6 outbox yayını, RabbitMQ tüketicileri ve `ClearPayOps` içindeki SQL'leri); reddedilen ödemeninki 38 span. Özel metrikler `clearpay_psp_calls_total{provider,operation,outcome}` ve `clearpay_threeds_results_total{status}` Prometheus'taydı.

Grafana'da (Tempo) reddedilen ödeme: 402 isteği, başarısız `psp simbank-rest authorize` span'ı ve 39 sn sonra aynı trace'teki outbox yayınları ile Operations servisinin tüketicileri:

![Grafana Tempo'da reddedilen ödeme trace'i](docs/assets/observability/grafana-tempo-trace-declined.png)

Aynı trace id Seq'te: iki servisin log olayları ve span'ları, `PSP Declined -> payment Declined` ve Operations servisinin `Notification card-payment-declined` kaydı dahil:

![Seq'te reddedilen ödemenin log ve span'ları](docs/assets/observability/seq-logs-declined-payment.png)

Tahsil edilen ödemenin tam trace'i [`docs/assets/observability/grafana-tempo-trace-captured.png`](docs/assets/observability/grafana-tempo-trace-captured.png) dosyasındadır. Bir ödeme başarısız olduğunda bunun nasıl kullanılacağı: [**Runbook: başarısız bir kart ödemesini izlemek**](docs/RUNBOOK-failed-payment.md) (İngilizce).

```bash
docker compose -f docker-compose.observability.yml up -d   # collector, Tempo, Prometheus, Grafana :3000, Seq :5341
# ardından iki host'u da şu ayarlarla başlatın
#   Otel__Endpoint=http://localhost:4317  Seq__Url=http://localhost:5341
```

Tüm gözlemlenebilirlik container'ları 127.0.0.1'e bağlanır ve kimlik doğrulamasız çalışır (Grafana anonim admin): yalnızca yerel demo içindir.

<a id="tr-api"></a>

### API Tasarımı

JSON API, JWT bearer kimlik doğrulaması, `/swagger` adresinde OpenAPI dokümanı. Hatalar RFC 7807 `application/problem+json` formatındadır.

| Metot | Yol | Notlar |
|--------|------|-------|
| POST | `/api/register` | Müşteri oluşturur, JWT döner (201) |
| POST | `/api/token` | E-posta + şifre -> JWT (8 saat) |
| POST | `/api/token/firebase` | Firebase ID token'ını JWT ile değiştirir (yalnızca Firebase yapılandırılmışsa) |
| POST | `/api/password/forgot`, `/api/password/reset` | Şifre kurtarma (demo mailer log'a yazar) |
| GET | `/api/wallet` | Bakiye (türetilmiş), aylık giriş/çıkış, son hareketler |
| POST | `/api/transfers` | `Idempotency-Key` zorunlu. 201 / 400 / 401 / 403 dondurulmuş / 404 alıcı / 409 tekrar / 422 yetersiz bakiye / 503 tekrar denenebilir |
| GET | `/api/transfers/{id}` | Yalnızca gönderen veya alıcı okuyabilir; diğerleri 404 alır |
| POST | `/api/topup`, `/api/withdraw` | `Idempotency-Key` zorunlu; simüle gateway zaman aşımına uğrarsa 202 |
| GET | `/api/movements` | Sayfalı (`page`, `pageSize` 1-50) |
| GET | `/api/receipts/{correlationId}` (`/pdf`) | Yalnızca hareketin tarafları |
| GET/POST | `/api/cards` | Bağlı kartlarda yalnızca son 4 hane, şema ve etiket saklanır (PAN ve CVV yok) |
| POST | `/api/3ds/sessions` | `Idempotency-Key` zorunlu. Simüle 3-D Secure doğrulamasını başlatır; `acsUrl` ile 201, tekrarda 409 |
| GET | `/api/3ds/sessions/{id}` | Oturum durumu; yalnızca sahibi (diğerleri 404) |
| GET/POST | `/fake-acs?creq=...` | Sahte banka doğrulama sayfası (anonim, HTML) |
| POST | `/api/3ds/callback` | İmzalı doğrulama sonucu (form post, anonim). 200 / 400 eski / 401 geçersiz imza / 409 / 410 süre doldu |
| GET | `/api/payments/providers` | Kayıtlı PSP adaptörlerinin adları |
| POST | `/api/payments` | `Idempotency-Key` zorunlu. `{ threeDsSessionId, provider?, capture? }`. 201 / 202 PSP zaman aşımı / 402 red / 409 tekrar veya 3DS zaten kullanılmış / 422 3DS doğrulanmamış |
| POST | `/api/payments/{id}/capture`, `/void` | `Idempotency-Key` zorunlu. 200 / 202 / 402 / 409 / 422 yanlış durum |
| POST | `/api/payments/{id}/refunds` | `Idempotency-Key` zorunlu. `{ amount }`, kısmi iade serbest. 200 / 202 / 402 red (defter ters kaydedilir) / 409 / 422 iade edilebilir tutarı veya cüzdan bakiyesini aşıyor |
| GET | `/api/payments/{id}` | İşlemleriyle birlikte ödeme; yalnızca sahibi |
| POST | `/api/admin/psp/settlement-files?date=yyyy-MM-dd` | `Admin` rolü: sahte PSP'ler o günün settlement dosyalarını hemen bırakır (aksi halde 01:00 UTC) |
| GET | Operations servisi `/api/reconciliation/runs`, `/runs/{id}` | Defter host'unun verdiği `Admin` JWT'si: çalıştırma özetleri; tek bir çalıştırmanın eşleşen / eşleşmeyen tüm kalemleri |
| POST | Operations servisi `/api/reconciliation/runs?date=yyyy-MM-dd` | `Admin`: bir iş gününü hemen mutabakata sokar (varsayılan dün, UTC) |
| GET | Operations servisi `/api/notifications?recipient=` | `Admin`: demo posta kutusu |
| GET/POST | `/api/admin/*` | `Admin` rolü: dondurma/çözme, outbox, audit arama |
| GET | `/api/health` | Canlılık ve Redis/RabbitMQ durumu |

Tasarım kararları:

- **Borçlandırılan cüzdan hiçbir zaman istek alanı değildir.** Gönderen her zaman JWT'deki `NameIdentifier` claim'idir; `fromUserId` gibi ek gövde alanları yok sayılır (testlerle doğrulanır).
- **Yabancılar 403 değil 404 alır**; böylece transfer ve dekont id'leri yoklanamaz.
- **Durum kodları istemcinin uygulayabileceği anlam taşır**: 409 = zaten yapıldı, 422 = iş kuralı, 503 = aynı anahtarla güvenle tekrar denenebilir.
- **İki kimlik doğrulama şeması, tek kullanıcı deposu**: Razor için cookie (antiforgery ile), API ve mobil için JWT; para API'si tarayıcı cookie'sini asla kabul etmez.

<a id="tr-stack"></a>

### Teknoloji Yığını

| Alan | Seçim |
|------|--------|
| Çalışma zamanı | .NET 8 (C# 12), tek host içinde ASP.NET Core Razor Pages + Web API; ayrıca ASP.NET Core Operations servisi |
| Veri | SQL Server 2022 (EF Core 8 migration'ları); hızlı entegrasyon testleri için SQLite |
| Kimlik | ASP.NET Core Identity, JWT bearer, isteğe bağlı Google/Apple/Firebase girişi |
| Doğrulama | FluentValidation |
| Arka plan işleri | Hangfire: defter host'u (outbox, dakikada bir 3-D Secure süre kontrolü, 01:00'da PSP settlement dosyaları); Operations servisi (02:30'da mutabakat) |
| Önbellek / mesajlaşma | Redis (StackExchange.Redis), RabbitMQ (RabbitMQ.Client: topic exchange, publisher confirms, TTL retry kuyrukları, DLQ, inbox), ikisi de isteğe bağlı |
| Gerçek zamanlı | SignalR (yalnızca yenileme sinyali, asla bakiye değil) |
| Dokümantasyon | Swashbuckle / OpenAPI |
| Mobil | Flutter (Dart 3), `http`, `flutter_secure_storage`, `signalr_netcore`, Firebase Auth (isteğe bağlı) |
| Gözlemlenebilirlik | Serilog (Seq sink), OpenTelemetry trace + metrik (OTLP -> collector -> Tempo, Prometheus, Seq), Grafana |
| Testler | xUnit, FluentAssertions, `WebApplicationFactory`, Flutter widget testleri |
| CI | SQL Server ve RabbitMQ service container'lı GitHub Actions |

<a id="tr-structure"></a>

### Depo Yapısı

```text
clearpay-ledger/
├── src/
│   ├── ClearPay.Domain/            # Defter varlıkları ve para kuralları, paket yok
│   ├── ClearPay.Application/       # Port'lar, komutlar/sonuçlar, validator'lar, mutabakat motoru
│   ├── ClearPay.Infrastructure/    # EF Core, migration'lar, Identity/JWT, gateway'ler, PSP'ler, outbox, önbellek
│   ├── ClearPay.Messaging/         # RabbitMQ topolojisi, retry/DLQ'lu tüketici host'u, inbox
│   ├── ClearPay.ServiceDefaults/   # Serilog + OpenTelemetry kurulumu
│   ├── ClearPay.Operations/        # Ayrı servis: bildirimler + mutabakat, kendi veritabanı
│   └── ClearPay.Web/               # Program.cs, API controller'ları, Razor Pages, SignalR hub
├── tests/
│   ├── ClearPay.Tests/             # xUnit birim, entegrasyon, mimari, SQL Server ve RabbitMQ testleri
│   └── ClearPay.Operations.Tests/  # Operations servisi ve servisler arası testler
├── mobile/clearpay/                # Flutter istemcisi (JWT), kendi README'si var
├── docs/                           # Ekran görüntüleri, runbook, SQL planları
├── scripts/sql/                    # Execution plan ölçüm scripti
├── docker-compose.yml              # Yerel SQL Server, Redis, RabbitMQ
├── docker-compose.observability.yml # Collector, Tempo, Prometheus, Grafana, Seq
├── .env.example                    # docker compose için yer tutucu değerler
└── .github/workflows/ci.yml        # .NET + Flutter CI
```

<a id="tr-tests"></a>

### Testler ve CI

#### Sonuçlar (yerelde ölçüldü)

| Paket | Komut | Altyapı | Sonuç |
|-------|---------|----------------|--------|
| .NET, tam (2026-11-09) | `CLEARPAY_TEST_SQLSERVER` ve `CLEARPAY_TEST_RABBITMQ` tanımlıyken `tests/ClearPay.Tests` ve `tests/ClearPay.Operations.Tests` üzerinde `dotnet test` | SQLite dosyaları (testler oluşturur) + Docker'da SQL Server 2022 + RabbitMQ 3.13 | **281 başarılı, 0 başarısız, 0 atlanan** (271 + 10) |
| .NET, hızlı (2026-11-09) | aynı komutlar, değişkenler tanımsız | yalnızca SQLite | 263 başarılı, 0 başarısız, 18 atlanan (defter 255 + 16 atlanan; Operations 8 + 2 atlanan) |
| Flutter (2026-11-03) | `mobile/clearpay` içinde `flutter analyze` ve `flutter test` | yok (mock HTTP) | analyzer uyarısı yok; **20 başarılı, 0 başarısız** |
| NuGet güvenlik açıkları | `dotnet list <project> package --vulnerable --include-transitive` | yok | güvenlik açığı olan paket yok |

Sayılar, test çalıştırıcılarının raporladığı test vakalarıdır (xUnit theory'leri her veri satırı için bir kez sayılır).

Para testlerinin kapsadıkları:

| Konu | Konum |
|---------|-------|
| Borç = alacak, bakiye kayıtlardan türetilir, her çiftin toplamı sıfırdır | `LedgerPairTests`, `LedgerInvariantTests`, `LedgerSafetyApiTests.Ledger_stays_balanced...` |
| Idempotent tekrar 409 döner ve kayıt oluşturmaz; aynı anahtar farklı gövdeyle | `TransferExecutorTests`, `TransferApiTests`, `LedgerSafetyApiTests.Errors_are_problem_details...` |
| Eşzamanlı tekrar istekler (aynı anahtar, 8 paralel) tam olarak bir transfer oluşturur | `SqlServerLedgerTests` (SQL Server) |
| Farklı anahtarlı eşzamanlı borçlar cüzdanı asla eksiye düşürmez | `SqlServerLedgerTests` (SQL Server) |
| Satırlar gönderildikten sonra veritabanı hata verirse rollback; anahtar kullanılabilir kalır | `TransferRollbackTests` (SQLite), `SqlServerLedgerTests` (SQL Server) |
| Yetersiz bakiye, dondurulmuş cüzdan, kendine transfer, bilinmeyen alıcı | `TransferExecutorTests`, `TransferApiTests`, `LedgerSafetyApiTests` |
| Kimlik doğrulama: eksik, sahte ve süresi dolmuş JWT | `TransferApiTests`, `LedgerSafetyApiTests` |
| Yetkilendirme: gövde borçlandırılacak cüzdanı seçemez; yabancılar transfer ve dekont okuyamaz | `LedgerSafetyApiTests`, `TransferApiTests` |
| Doğrulama: tutar <= 0 veya 2'den fazla ondalık, bozuk JSON, aşırı uzun anahtar/açıklama | `LedgerSafetyApiTests`, `SqlServerLedgerTests` |
| Audit satırı ve bekleyen outbox satırı defter correlation id'sini paylaşır; red durumunda yazılmaz | `LedgerSafetyApiTests` |
| 3-D Secure: durum makinesi, imzalı CReq/CRes, sahte / eski / tekrar oynatılan callback'ler, süre dolumu, tam tarayıcı akışı | `ThreeDsServiceTests`, `ThreeDsDomainAndSignerTests`, `ThreeDsApiTests` |
| 3-D Secure: 8 çelişen paralel callback tam olarak bir geçiş ve bir outbox olayı üretir | `ThreeDsSqlServerTests` (SQL Server) |
| PSP adaptörleri: üçünde de yetkilendirme + tahsilat; redler kayıt oluşturmaz; tahsilattan önce iptal; kısmi iadeler sınırlı; iade reddi ters kaydedilir; kayıp PSP yanıtı çift kayıt olmadan tamamlanır; tekrar oynatılan anahtarlar | `CardPaymentServiceTests`, `CardPaymentApiTests` |
| 100,00'lık bir ödemede on paralel 30,00 iade: tam olarak üçü başarılı; CHECK kısıtı fazla iadeyi reddeder | `CardPaymentSqlServerTests` (SQL Server) |
| Mutabakat eşleştirme kuralları ve settlement CSV | `ReconciliationEngineTests` |
| Operations servisi: defter veritabanına dokunmadan gerçek defter ödemeleri, olaylar ve PSP dosyalarından her uyuşmazlık türü; eksik dosya raporu; gece job'ı dünü hedefler; atlama / zorlama | `ReconciliationAcrossServicesTests` (Operations.Tests) |
| Operations servisi: olay başına bir bildirim, operatör raporu, sırası karışık projeksiyon, zehirli olaylar, defterin JWT'siyle yalnızca admin API | `NotificationAndProjectionTests`, `OperationsApiTests` (Operations.Tests), `PspSimulatorAdminApiTests` |
| Gerçek broker üzerinden iki host: defter outbox'ı -> RabbitMQ -> Operations projeksiyonu + bildirim; tam yeniden yayın tek bildirim üretir | `CrossServiceRabbitTests` (Operations.Tests, RabbitMQ) |
| SQL Server'da Operations migration'ı ve `ops.usp_SettlementCandidates`; mimari: Operations deftere asla referans vermez | `OperationsSqlServerTests` (SQL Server), `ArchitectureTests` |
| Tracing: outbox satırı isteğin traceparent'ını korur ve outbox yayını o trace'i sürdürür; AMQP header'ları bağlamı taşır | `TracingTests` |
| Inbox: aynı mesaj iki kez gelse de bir kez yazılır; başarısız handler inbox satırını geri alır; zehirli içerik | `InboxProcessorTests` |
| RabbitMQ: outbox'ın tekrar yayını ve ack öncesi çökme sonrası broker teslimi bir kez yazılır; retry'lar 200 / 400 ms bekleyip bir kez başarılı olur; tükenen retry'lar ve zehirli mesajlar DLQ'ya düşer | `RabbitMessagingTests` (RabbitMQ) |
| Outbox: başarısız yayın deneme sınırına kadar `Pending` kalır, toplu işlem durur, mesaj id = satır id | `OutboxProcessorTests`, `RabbitOutboxPublisherTests` |
| Stored procedure'ler yerini aldıkları LINQ ile aynı bakiye / özet / settlement satırlarını döner, çağıranın transaction'ına katılır ve covering index'ler migration sonrasında mevcuttur | `LedgerProcedureSqlServerTests` (SQL Server) |
| Outbox worker'ı, gateway zaman aşımında outbox satırının korunması | `OutboxProcessorTests`, `FundingExecutorTests` |
| Veritabanı CHECK kısıtı sıfır tutarlı satırları reddeder | `SqlServerLedgerTests` |
| Katmanlar arası mimari kurallar | `ArchitectureTests` |

#### Testleri çalıştırma

```bash
# Hızlı paket: yalnızca SQLite, Docker gerekmez (SQL Server ve RabbitMQ testleri atlanmış olarak raporlanır)
dotnet test tests/ClearPay.Tests/ClearPay.Tests.csproj -c Release
dotnet test tests/ClearPay.Operations.Tests/ClearPay.Operations.Tests.csproj -c Release

# Tam paket: tek kullanımlık SQL Server ve RabbitMQ container'ları
docker run -d --name clearpay-test-sql -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=<local-password>" \
  -p 127.0.0.1:14339:1433 mcr.microsoft.com/mssql/server:2022-latest
export CLEARPAY_TEST_SQLSERVER="Server=127.0.0.1,14339;User Id=sa;Password=<local-password>;TrustServerCertificate=True;Encrypt=True"
docker run -d --name clearpay-test-rabbit -p 127.0.0.1:25672:5672 rabbitmq:3.13-management-alpine
export CLEARPAY_TEST_RABBITMQ="amqp://guest:guest@127.0.0.1:25672/"
dotnet test tests/ClearPay.Tests/ClearPay.Tests.csproj -c Release
dotnet test tests/ClearPay.Operations.Tests/ClearPay.Operations.Tests.csproj -c Release

# Flutter
cd mobile/clearpay
cp firebase_options.template.dart lib/firebase_options.dart
flutter pub get && flutter analyze && flutter test
```

Her SQL Server test sınıfı kendi veritabanını (`ClearPayTest_<random>`) oluşturur, gerçek EF migration'larını uygular ve sonunda veritabanını siler.

#### CI

`.github/workflows/ci.yml`, `main` dalına yapılan push'larda ve pull request'lerde çalışır:

1. **.NET job'ı** (`mcr.microsoft.com/mssql/server:2022-latest` ve `rabbitmq:3.13-management-alpine` service container'ları ile): restore, build ve iki test projesinin testi (Release), `CLEARPAY_TEST_SQLSERVER` ve `CLEARPAY_TEST_RABBITMQ` tanımlıyken (eşzamanlılık, rollback ve mesajlaşma testleri gerçek sunuculara karşı çalışır), TRX sonuçlarını yükleme; `dotnet list package --vulnerable --include-transitive` herhangi bir bulgu raporlarsa job başarısız olur.
2. **Flutter job'ı**: Firebase options şablonunu kopyalama, `flutter pub get`, `flutter analyze`, `flutter test`.

<a id="tr-install"></a>

### Kurulum

Gereksinimler: .NET SDK 8 veya üzeri, Docker (ya da Windows'ta SQL Server LocalDB), mobil istemci için Flutter.

```bash
# 1. Altyapı (SQL Server, Redis, RabbitMQ)
cp .env.example .env            # MSSQL_SA_PASSWORD değerini kendi yerel değerinizle değiştirin
docker compose up -d

# 2. Yerel gizli ayarlar (asla commit edilmez)
dotnet user-secrets set "ConnectionStrings:ClearPay" \
  "Server=localhost,1433;Database=ClearPay;User Id=sa;Password=<your MSSQL_SA_PASSWORD>;TrustServerCertificate=True;Encrypt=True" \
  --project src/ClearPay.Web
dotnet user-secrets set "Jwt:SigningKey" "<at least 32 random characters>" --project src/ClearPay.Web
dotnet user-secrets set "ThreeDs:AcsSharedSecret" "<at least 32 random characters>" --project src/ClearPay.Web   # Development'ta isteğe bağlı
dotnet user-secrets set "Seed:AdminPassword" "<password for the local admin account>" --project src/ClearPay.Web   # isteğe bağlı

# 3. Çalıştırma (migration'lar başlangıçta uygulanır)
dotnet run --project src/ClearPay.Web --launch-profile http
# Web arayüzü: http://localhost:5153   Swagger: http://localhost:5153/swagger
```

Notlar:

- `Development` ortamında `appsettings.Development.json` SQL Server LocalDB'yi (`(localdb)\MSSQLLocalDB`) kullanır; Windows'ta Docker'ı ve connection string secret'ını atlayabilirsiniz.
- `Jwt:SigningKey` tanımlı değilse Development herkese açık bir yer tutucu anahtara düşer; diğer tüm ortamlar anahtar yapılandırılmadan başlamayı reddeder.
- `Seed:AdminPassword` yalnızca Development ortamında `Admin` rolüne sahip `admin@clearpay.test` hesabını oluşturur. Tanımlı değilse admin hesabı oluşturulmaz; normal şekilde kayıt olun.
- Google, Apple ve Firebase girişi ayarları verilene kadar kapalı kalır (`Authentication:Google:*`, `Authentication:Apple:*`, `Firebase:ProjectId`).
- Bildirimler ve mutabakat için Operations servisini de çalıştırın: bkz. [Operations Servisi](#tr-operations).
- Mobil istemci: [`mobile/clearpay/README.md`](mobile/clearpay/README.md) dosyasına bakın.

<a id="tr-screenshots"></a>

### Demo Ekran Görüntüleri

| | |
|---|---|
| ![Cüzdan özeti](docs/assets/shot-ozet.png) | ![Kartlarım](docs/assets/shot-kartlar.png) |
| Cüzdan özeti: defterden türetilen bakiye, son hareketler · Wallet overview: balance derived from the ledger, last movements | Kartlarım: yalnızca son dört hane, şema ve etiket saklanır; görünen numara standart Visa test numarasıdır · Linked cards: only last four digits, scheme and label are stored |

İki ekran görüntüsü de Türkçe arayüzde, `.test` alan adındaki yerel Development seed hesabını gösterir; gerçek kişisel veri içermez. Gözlemlenebilirlik ekran görüntüleri [Gözlemlenebilirlik](#tr-observability) bölümündedir.

<a id="tr-security"></a>

### Güvenlik Notları

- **Depoda gerçek gizli bilgi yoktur.** Connection string'ler, JWT imzalama anahtarı, 3-D Secure paylaşılan anahtarı, OAuth ve Firebase ayarları user-secrets veya ortam değişkenlerinden gelir. `appsettings*.json` ve `.env.example` yalnızca boş değerler veya yerel kullanım için yer tutucular içerir; CI workflow'u geçici container'ları için tek kullanımlık şifreler kullanır. Firebase istemci dosyaları `flutterfire configure` ile yerelde üretilir ve gitignore kapsamındadır.
- **Yapısal yetkilendirme.** Borçlandırılan cüzdan istek gövdesinden değil token'dan gelir. Transferler, dekontlar, 3-D Secure oturumları ve ödemeler sahipleriyle sınırlıdır (diğerleri için 404). Admin endpoint'leri `Admin` rolü gerektirir.
- **JWT**: HMAC-SHA256; issuer, audience, imza ve süre doğrulanır (1 dakika saat kayması toleransı); refresh token veya sunucu tarafı iptal olmadan 8 saatlik token'lar.
- **Kart verisi**: yalnızca son dört hane, şema ve etiket saklanır; PAN ve CVV hiçbir zaman kalıcı olarak tutulmaz.
- **Veri bütünlüğü** kodun yanı sıra veritabanı kısıtlarına da dayanır: benzersiz idempotency anahtarı, sıfır olmayan tutar CHECK kısıtı, iade ve tahsilat CHECK kısıtları, silmeyi kısıtlayan foreign key'ler.
- **Demo ötesi için bilinen eksikler**: giriş kilitleme veya rate limiting yok, Swagger tüm ortamlarda açık, forwarded header'lara her proxy'den güveniliyor, SignalR JWT'yi query string'de taşıyor (WebSocket için standart, ancak proxy log'larında görünebilir); 2FA, KYC veya AML yok. Gözlemlenebilirlik container'ları kimlik doğrulamasız çalışır ve yalnızca yerel içindir.

<a id="tr-limitations"></a>

### Sınırlamalar

- Portföy demosudur. Lisanslı bir finansal ürün değildir; denetlenmemiştir; gerçek banka veya kart ağı entegrasyonu yoktur (gateway, PSP'ler ve 3-D Secure bankası simüle edilmiştir).
- Tek para birimi (TRY); kur çevrimi, ücret, faiz, hesap özeti, limit veya zamanlanmış ödeme yoktur.
- Bakiye her kontrolde hâlâ cüzdanın tüm kayıtlarının toplamıdır (artık tek bir covering index seek: 100.000 satırlık bir cüzdan için yaklaşık 1.000 logical read; bkz. [T-SQL](#tr-tsql)). Üretim seviyesindeki bir defter, hesap başına bakiye snapshot'ları veya kilitli bir yürüyen bakiye ekler.
- Serializable izolasyon eksi bakiye kontrolünü doğru tutar, ancak çekişmeyi deadlock tekrarlarına (503) dönüştürür. Gönderen hesabın satır seviyesinde kilitlenmesi daha iyi ölçeklenir.
- Eşzamanlı tekrar yükleme istekleri, biri idempotency eklemesini kazanmadan önce simüle gateway'e ulaşabilir; defter yine yalnızca bir kez kayıt yapar. Gerçek bir entegrasyon anahtarı önce rezerve eder ve sağlayıcıya idempotency anahtarı olarak iletir.
- Mutabakat, işlemleri PSP çağrısının başladığı UTC tarihine göre gruplar; gece yarısını aşan bir tekrar deneme iki tarafta farklı günlere düşüp iki uyuşmazlık olarak görünebilir. Settlement dosyaları SFTP yerine simülatörlerden yerel bir klasör üzerinden gelir; demoda iki servis de tek makinede çalışır.
- Outbox worker'ı en az bir kez teslimat yapan tek bir Hangfire job'ıdır; birden fazla rekabet eden worker için tasarlanmamıştır.
- Kart iadeleri vardır (`/api/payments/{id}/refunds`); cüzdandan cüzdana transferler API veya arayüz üzerinden iade edilemez. Kart ödemeleri için henüz web arayüzü yoktur (yalnızca API ve sahte ACS sayfası).
- Şifre sıfırlama e-postası yalnızca log'a yazılır (`LogAccountMailer`); gerçek e-posta gönderimi yoktur.
- Hızlı test paketi SQLite kullanır; SQL Server'a özgü davranışlar (izolasyon, deadlock, kısıtlar) yalnızca SQL Server testleriyle kapsanır.
- Yayına alınmamıştır; yerelde çalışır.

<a id="tr-license"></a>

### Lisans

[MIT](LICENSE) (c) 2026 Halil Mert Develi.

Üçüncü taraf bileşenler kendi lisanslarını korur: `src/ClearPay.Web/wwwroot/js/vendor` altındaki `anime.js` 3.2.2 (MIT) ve `@microsoft/signalr` tarayıcı istemcisi (MIT); Inter yazı tipi Google Fonts üzerinden yüklenir (SIL Open Font License). NuGet ve pub bağımlılıkları kendi kayıt depolarından indirilir ve MIT, Apache-2.0 veya BSD lisanslıdır; Hangfire ise istisnadır (LGPL-3.0, kütüphane olarak değiştirilmeden kullanılır).

<p align="right"><a href="#turkce">Başa dön</a> · <a href="#english">English</a></p>
