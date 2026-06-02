# Payment Webhook — F#

A small, **functional-first** HTTP(S) service that receives payment-confirmation
webhooks from a payment gateway (PayPal / MercadoPago style), validates them,
and confirms or cancels the transaction by calling back into the gateway.

It is written in **F#** on top of **ASP.NET Core + [Giraffe](https://giraffe.wiki/)**,
with **SQLite** for persistence. The design follows functional principles: pure
validation logic, immutable domain types, and side effects isolated into small,
explicit modules.

---

## What it does

When a payment is confirmed, the gateway sends a `POST` to `/webhook` with a JSON
payload:

```json
{
  "event": "payment_success",
  "transaction_id": "abc123",
  "amount": 49.90,
  "currency": "BRL",
  "timestamp": "2025-05-11T16:00:00Z"
}
```

The service then:

1. **Authenticates** the request via a shared secret in the `X-Webhook-Token` header.
2. **Validates the payload integrity** (well-formed JSON object + all mandatory fields).
3. **Checks the business rule** (expected amount `49.90` and currency `BRL`).
4. **Guarantees idempotency** — a transaction already confirmed in the database is never confirmed twice.
5. **Confirms** valid transactions (HTTP call to the gateway `/confirmar` + persists to the DB) or **cancels** divergent ones (HTTP call to `/cancelar`).

### Decision rules

| Situation | HTTP status | Side effect |
|---|---|---|
| Everything valid | `200` | Confirm transaction + persist |
| Invalid / missing token (forged) | `403` | **Ignored** (never cancelled) |
| Body is not a valid JSON object | `400` | None |
| Missing `transaction_id` | `400` | **Not** cancelled (nothing to cancel) |
| Other missing field | `400` | Cancel |
| Duplicate transaction | `400` | Cancel |
| Wrong amount / currency | `400` | Cancel |

---

## Project structure

```
.
├── Webhook.fsproj         # Project file + compilation order + dependencies
├── src/
│   ├── Domain.fs          # Immutable domain types (RawPayload, Payment, WebhookError)
│   ├── Database.fs        # SQLite persistence + idempotency (side effects isolated)
│   ├── Gateway.fs         # Outbound HTTP calls: /confirmar and /cancelar
│   ├── Validation.fs      # Pure, railway-oriented validation pipeline
│   └── Program.fs         # JSON parsing, Giraffe handler, Kestrel (HTTP + HTTPS)
├── test_webhook.py        # Provided minimum test harness
└── README.md
```

The dependency flow is one-directional and pure-to-impure friendly:
`Domain → Database → Gateway → Validation → Program`.

---

## Requirements

- [.NET SDK 10.0+](https://dotnet.microsoft.com/download) (`dotnet --version`)
- Python 3.10+ (only to run the provided `test_webhook.py`)

---

## Install & run

From the project root:

```bash
# Restore dependencies and start the service
dotnet run --project Webhook.fsproj
```

The service starts two listeners:

- **HTTP**:  `http://localhost:5000`  (used by the grading test harness)
- **HTTPS**: `https://localhost:5443` (self-signed certificate generated in-memory at startup)

Health check:

```bash
curl http://localhost:5000/health        # -> ok
curl -k https://localhost:5443/health     # -> ok (TLS)
```

### Configuration (optional environment variables)

| Variable | Default | Purpose |
|---|---|---|
| `WEBHOOK_TOKEN` | `meu-token-secreto` | Shared secret expected in `X-Webhook-Token` |
| `GATEWAY_URL` | `http://127.0.0.1:5001` | Base URL the service calls to confirm/cancel |

---

## Running the tests

The provided `test_webhook.py` starts its own mock gateway on port `5001`,
sends six scenarios to the webhook on port `5000`, and reports the results.

```bash
# 1. Create a virtual env and install the test dependencies
python3 -m venv .venv
.venv/bin/pip install fastapi uvicorn requests

# 2. In one terminal, start the webhook service
dotnet run --project Webhook.fsproj

# 3. In another terminal, run the tests
.venv/bin/python test_webhook.py
```

Expected output:

```
1. Webhook test "Token Inválido": ok
2. Webhook test "Payload Vazio": ok
3. Webhook test "Campos Ausentes": ok
4. Webhook test "Fluxo Correto": ok
5. Webhook test "Transação Duplicada": ok
6. Webhook test "Amount Incorreto": ok
6/6 tests completed.
```

> **Note:** because idempotency is persisted, delete the local database
> (`rm -f webhook.db`) before re-running the suite, so transaction `abc123`
> starts out unconfirmed.

### ⚠️ macOS port 5000 note

On recent macOS versions, **AirPlay Receiver** (served by `ControlCenter`)
listens on port `5000`, which the test harness hard-codes. If the test cannot
reach the webhook, free the port by disabling it:

**System Settings → General → AirDrop & Handoff → AirPlay Receiver → Off**

(The service binds the specific loopback `127.0.0.1:5000`, so it generally wins
over AirPlay's wildcard bind, but disabling AirPlay Receiver is the reliable fix.)

---

## Mapping to the assignment rubric

This implementation targets **grade A** by covering the minimum test plus every
optional item:

- ✅ **Minimum test** — passes 6/6.
- ✅ **Payload integrity** — `Validation.fs` validates JSON shape and all required fields.
- ✅ **Transaction veracity** — shared-secret token authentication (`X-Webhook-Token`).
- ✅ **Cancel on divergence** — missing fields / wrong amount / duplicates trigger `/cancelar`.
- ✅ **Confirm on success** — valid transactions trigger `/confirmar`.
- ✅ **Persistence in a DB** — SQLite (`webhook.db`), which also enforces idempotency.
- ✅ **HTTPS service** — Kestrel HTTPS endpoint on port `5443`.

---

## Why functional programming?

- **Pure functions** (`Validation.fs`): business rules are total, deterministic, and trivially testable.
- **Immutability** (`Domain.fs`): the validated `Payment` type is proof that all checks passed — illegal states are unrepresentable.
- **Railway-oriented composition**: validation steps chain with `Result.bind`, short-circuiting on the first error.
- **Isolated side effects**: all I/O (DB, outbound HTTP) lives in dedicated modules, keeping the core logic free of hidden state.
