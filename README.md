# ClearPay Ledger

A digital-wallet backend built around a **double-entry ledger**, **idempotent money APIs** and
**single-transaction consistency**, with an ASP.NET Core Razor web UI and a Flutter mobile client
on top of the same API.

> **Portfolio demo, not a banking product.** ClearPay is not a licensed bank, payment institution
> or e-money issuer. No real money moves: the bank gateway used for top-up and withdrawal is a
> simulated REST/SOAP stub. Amounts are demo Turkish lira (TRY). The UI defaults to Turkish and
> also ships English, German and French.

---

## Problem & Solution

**Problem.** The classic wallet bug is `UPDATE Wallet SET Balance = Balance - @amount`. It loses
history, cannot be audited, double-charges when a mobile client retries after a timeout, and
leaves money half-moved when the process dies between the debit and the credit.

**Solution.** ClearPay never stores a balance. Every movement is a pair of signed ledger rows
(one debit, one credit, summing to zero) written in **one SQL Server transaction** together with
the transfer record, the idempotency key, an audit row and an outbox message. The balance is the
sum of a wallet's ledger rows. A replayed request with the same `Idempotency-Key` returns
**409 Conflict** and posts nothing.

## Architecture

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
| `mobile/clearpay` | Flutter client (JWT); no local balance | Any money logic |

Redis and RabbitMQ are optional: without Redis the summary is read from SQL; without RabbitMQ the
outbox publisher logs instead of publishing. SQL Server is the only source of truth.

## Core Banking Concepts Demonstrated

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

## Double-Entry Ledger

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
  restrict-delete foreign keys, and an index on `(WalletId, CreatedAt)`.
- Balance = `SUM(Amount) WHERE WalletId = @wallet`. Wallet summaries may be cached in Redis but
  are invalidated after every commit and never written back to SQL.

Why no balance column? A stored balance is a second source of truth that can drift from the
entries. Deriving it makes "balance equals the sum of entries" true by construction; the cost is a
read over the wallet's rows, acceptable at demo scale (see Limitations).

## Idempotency

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

## Transaction Consistency

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
- The outbox worker (Hangfire, every minute) marks rows `Sent` or `Failed`; failed rows can be
  re-queued from the admin API. Delivery is at-least-once, so consumers should deduplicate by
  correlation id.

## Card Payments: 3-D Secure (simulated)

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

## Card Payments: PSP Adapters, Capture, Refund, Void

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

## API Design

JSON API, JWT bearer auth, OpenAPI document at `/swagger`. Errors use RFC 7807
`application/problem+json`.

| Method | Path | Notes |
|--------|------|-------|
| POST | `/api/register` | Creates a customer, returns a JWT (201) |
| POST | `/api/token` | Email + password -> JWT (8 h) |
| POST | `/api/token/firebase` | Exchanges a Firebase ID token for a JWT (only if Firebase is configured) |
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

## Technology Stack

| Area | Choice |
|------|--------|
| Runtime | .NET 8 (C# 12), ASP.NET Core Razor Pages + Web API in one host |
| Data | SQL Server 2022 (EF Core 8 migrations); SQLite for fast integration tests |
| Identity | ASP.NET Core Identity, JWT bearer, optional Google/Apple/Firebase sign-in |
| Validation | FluentValidation |
| Background jobs | Hangfire (outbox processing) |
| Cache / messaging | Redis (StackExchange.Redis), RabbitMQ (RabbitMQ.Client), both optional |
| Realtime | SignalR (refresh hints only, never balances) |
| Docs | Swashbuckle / OpenAPI |
| Mobile | Flutter (Dart 3), `http`, `flutter_secure_storage`, `signalr_netcore` |
| Tests | xUnit, FluentAssertions, `WebApplicationFactory`, Flutter widget tests |
| CI | GitHub Actions with a SQL Server service container |

## Tests & CI

### Results (measured locally)

| Suite | Command | Infrastructure | Result |
|-------|---------|----------------|--------|
| .NET, with SQL Server (2026-11-09) | `dotnet test tests/ClearPay.Tests/ClearPay.Tests.csproj -c Release` with `CLEARPAY_TEST_SQLSERVER` set | SQLite files (created by the tests) + SQL Server 2022 in Docker | **246 passed, 0 failed, 0 skipped** |
| .NET, without SQL Server (2026-11-09) | same command, variable unset | SQLite only | 238 passed, 0 failed, 8 skipped (the SQL Server tests) |
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
| Outbox worker, gateway timeout keeps the outbox row | `OutboxProcessorTests`, `FundingExecutorTests` |
| Database CHECK constraint rejects zero-amount rows | `SqlServerLedgerTests` |
| Architecture rules between layers | `ArchitectureTests` |

### Run the tests

```bash
# Fast suite: SQLite only, no Docker needed (SQL Server tests are reported as skipped)
dotnet test tests/ClearPay.Tests/ClearPay.Tests.csproj -c Release

# Full suite: start a throw-away SQL Server and point the tests at it
docker run -d --name clearpay-test-sql -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=<local-password>" \
  -p 127.0.0.1:14339:1433 mcr.microsoft.com/mssql/server:2022-latest
export CLEARPAY_TEST_SQLSERVER="Server=127.0.0.1,14339;User Id=sa;Password=<local-password>;TrustServerCertificate=True;Encrypt=True"
dotnet test tests/ClearPay.Tests/ClearPay.Tests.csproj -c Release

# Flutter
cd mobile/clearpay
cp firebase_options.template.dart lib/firebase_options.dart
flutter pub get && flutter analyze && flutter test
```

Each SQL Server test class creates its own database (`ClearPayTest_<random>`), applies the real
EF migrations and drops it afterwards.

### CI

`.github/workflows/ci.yml` runs on pushes to `main` and on pull requests:

1. **.NET job** with a `mcr.microsoft.com/mssql/server:2022-latest` service container: restore,
   build (Release), test with `CLEARPAY_TEST_SQLSERVER` set (so the concurrency and rollback tests
   run against SQL Server), upload TRX results, then fail the job if
   `dotnet list package --vulnerable --include-transitive` reports anything.
2. **Flutter job**: copy the Firebase options template, `flutter pub get`, `flutter analyze`,
   `flutter test`.

## Installation

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
- Mobile client: see [`mobile/clearpay/README.md`](mobile/clearpay/README.md).

## Demo Screenshots

Wallet overview (balance derived from the ledger, last movements):

![Wallet overview](docs/assets/shot-ozet.png)

Linked cards (only last four digits, scheme and label are stored; the number shown is the
standard Visa test number):

![Linked cards](docs/assets/shot-kartlar.png)

Both screenshots show the local Development seed account on a `.test` domain; no real personal data.

## Security Considerations

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

## Limitations

- Portfolio demo. Not a licensed financial product; not audited; no real bank or card network
  integration (the gateway is simulated).
- Single currency (TRY), no FX, fees, interest, statements, limits or scheduled payments.
- Balance is computed by summing all of a wallet's entries on each check. A production ledger
  would add balance snapshots or a locked running balance per account.
- Serializable isolation keeps the overdraft check correct but converts contention into deadlock
  retries (503). Row-level locking of the sender's account would scale better.
- Concurrent duplicate top-up requests can both reach the simulated gateway before one of them
  wins the idempotency insert; the ledger still posts once. A real integration would reserve the
  key first and pass it to the provider as its idempotency key.
- The outbox worker is a single Hangfire job with at-least-once delivery; it is not designed for
  multiple competing workers.
- Card refunds exist (`/api/payments/{id}/refunds`); wallet-to-wallet transfers cannot be refunded through the API or UI. There is no web UI for card payments yet (API and the fake ACS page only).
- The fast test suite uses SQLite; SQL Server-specific behaviour (isolation, deadlocks,
  constraints) is covered only by the SQL Server tests.

## License

[MIT](LICENSE) (c) 2026 Halil Mert Develi.

Third-party components keep their own licences: vendored `anime.js` 3.2.2 (MIT) and the
`@microsoft/signalr` browser client (MIT) under `src/ClearPay.Web/wwwroot/js/vendor`; the Inter
font is loaded from Google Fonts (SIL Open Font License). NuGet and pub dependencies are restored
from their registries and are MIT, Apache-2.0 or BSD licensed, except Hangfire (LGPL-3.0, used
unmodified as a library).
