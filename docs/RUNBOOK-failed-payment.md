# Runbook: tracing a failed card payment

Demo runbook for ClearPay (portfolio project; demo data only). It shows how one payment is followed
across the ledger host and the Operations service with logs (Seq), traces (Tempo via Grafana) and the
database rows that are the source of truth. Screenshots: [`docs/assets/observability`](assets/observability).

## 0. What you have

| Signal | Where | Key |
|--------|-------|-----|
| Trace id | Every HTTP response: `traceparent` header; every error body: `traceId` (RFC 7807) | `00-<trace id>-<span id>-01` |
| Correlation id | Payment / receipt / outbox rows, audit log, events | `correlationId` |
| Logs | Seq `http://localhost:5341` - every event carries `TraceId`, `SpanId`, `Service` | `TraceId = '<trace id>'` |
| Traces | Grafana `http://localhost:3000` -> Explore -> Tempo (also visible in Seq) | trace id |
| Metrics | Prometheus `http://localhost:9090`: `clearpay_psp_calls_total{outcome!="Approved"}` | provider, operation, outcome |
| State | `CardPayment`, `PaymentOperation`, `OutboxMessage` (ledger DB); `ops.PaymentOperation`, `ops.Notification`, `ops.ReconciliationItem` (Operations DB) | payment id / operation id |

## 1. Get the trace id

- The client got an error such as **402 Declined by the provider**. The body contains
  `"traceId": "00-865d29d4ab0188741aa9ce98df8c2697-..."` and `"paymentId"`. Ask support for that line.
- Only a payment id? `GET /api/payments/{id}` (as the customer) or the `CardPayment` row gives the
  `correlationId`; in Seq search `CorrelationId = '<id>'` or `@Message like '%<payment id>%'` and read
  `TraceId` from any event.

## 2. Read the trace (Grafana -> Explore -> Tempo -> paste the trace id)

A healthy captured payment is **one trace over both services**:

```text
clearpay-ledger      POST /api/payments                         (ASP.NET Core)
clearpay-ledger        psp simbank-rest authorize               (ClearPay.Payments, client span)
clearpay-ledger        SQL ...                                  (SqlClient: idempotency, CardPayment, outbox)
clearpay-ledger        psp simbank-rest capture
clearpay-ledger        SQL dbo.... / ledger pair + outbox
  ... up to a minute later (Hangfire outbox job, continues the stored traceparent) ...
clearpay-ledger      outbox clearpay.payment.captured           (ClearPay.Outbox, producer)
clearpay-ledger        clearpay.payment.captured publish        (ClearPay.Messaging, producer -> RabbitMQ)
clearpay-operations      clearpay.ops.notifications process     (ClearPay.Messaging, consumer)
clearpay-operations        SQL ops.InboxMessage / ops.Notification
clearpay-operations      clearpay.ops.payment-projection process
```

What the failure looks like and what it means:

| What you see | Meaning | Next step |
|--------------|---------|-----------|
| `psp ... authorize` span in **error**, tag `clearpay.psp.decline_code` (e.g. `51`) | The PSP declined; the ledger was not touched (no ledger pair, payment `Declined`) | Nothing to repair. Tell the customer the bank declined (code 51 = insufficient funds in the simulator). |
| `psp ... capture` span in error with `TimedOut` | The PSP answer was lost; operation stays `Pending`, payment `Authorized`, response 202 | Client retries with the same `Idempotency-Key` (provider key is deterministic, so the PSP returns the stored result). If nobody retries, the nightly reconciliation reports `PendingConfirmedBySettlement` or `PendingNotInSettlement`. |
| Request span ends with 409 | Replay of an `Idempotency-Key` | Not a failure: the original result is in the body. |
| `outbox ...` span in error, `clearpay.outbox.attempts` growing | Broker unreachable; the event is still in `OutboxMessage` (Pending) | Check RabbitMQ (`/api/health` -> `rabbit`). After 5 attempts the row is `Failed`: re-queue it with `POST /api/admin/outbox/{id}/requeue` once the broker is back. |
| No `clearpay-operations` spans | Event not consumed yet, or parked | RabbitMQ UI: `clearpay.ops.*.retry.*` (waiting for retry) or `clearpay.ops.*.dlq` (header `x-last-error`). Fix the cause and shovel the message back to the main queue. Redelivery is safe (inbox). |
| Consumer span in error, then later spans with `messaging.rabbitmq.attempt` = 2, 3 | Transient failure retried with backoff | Nothing, unless it ends in the DLQ. |

## 3. Read the logs for the same trace (Seq)

```text
TraceId = '865d29d4ab0188741aa9ce98df8c2697'
```

Expect, in order: the request log line (`HTTP POST /api/payments responded 402`), `Payment {PaymentId}
operation {OperationId}: PSP Declined -> payment Declined`, and - once the outbox has run - the
Operations service's `Notification card-payment-declined to {Recipient}`. Every event shows its
`Service` (`clearpay-ledger` or `clearpay-operations`).

## 4. Confirm the money (the database is the truth, not the logs)

```sql
-- Ledger DB
SELECT Status, Amount, CapturedAmount, RefundedAmount, DeclineCode FROM CardPayment WHERE Id = @paymentId;
SELECT Type, Status, ProviderReference, Amount FROM PaymentOperation WHERE PaymentId = @paymentId ORDER BY CreatedAt;
SELECT WalletId, Amount, Kind FROM LedgerEntry WHERE CorrelationId = @correlationId;   -- declined: no rows
SELECT Type, Status, Attempts, LastError FROM OutboxMessage WHERE CorrelationId = @correlationId;
-- Operations DB
SELECT Type, Status FROM ops.PaymentOperation WHERE PaymentId = @paymentId;
SELECT Template, Text FROM ops.Notification WHERE CorrelationId = @correlationId;
```

## 5. Next morning: reconciliation

`GET /api/reconciliation/runs` on the Operations service (admin JWT) lists the night's runs;
`/runs/{id}` lists every mismatch with provider reference and operation id - the operation id leads
back to step 4. Kinds and actions are in the README ("Operations Service").

## Reproduce locally

```bash
docker compose up -d sql rabbitmq
docker compose -f docker-compose.observability.yml up -d
# both hosts, with: Otel__Endpoint=http://localhost:4317  Seq__Url=http://localhost:5341
dotnet run --project src/ClearPay.Web --launch-profile http
dotnet run --project src/ClearPay.Operations --launch-profile http
```

A declined payment: authenticate 3-D Secure for **75.51** TRY (any Luhn-valid test card, OTP
`123456`) and pay with `simbank-rest` - the simulator declines amounts ending in `.51` with code `51`.
