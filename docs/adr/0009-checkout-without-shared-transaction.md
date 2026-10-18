# 0009. Checkout coordinated across modules without a shared transaction

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0002](0002-module-boundary-rules.md) · [0007](0007-module-communication.md) · [0010](0010-hold-lifetimes-and-payment-expiry.md) · [payments.md](../payments.md)

## Context

The first version of [payments.md](../payments.md) described steps 1 and 4 as *one database
transaction* across Inventory, Orders and Payments. That contradicts the rule of one schema,
DbContext and login per module ([0002](0002-module-boundary-rules.md)), and it would block
moving Inventory to its own database server later
([0011](0011-seat-inventory-concurrency.md)).

## Decision

Each module commits its own transaction. Orders coordinates the steps so that a crash at any
point can be recovered. No distributed transaction is needed, because a hold expires on its
own: if anything after it fails, the hold simply runs out.

```
fan ─► PublicApi POST /orders {holdId}        Idempotency-Key; one order per hold
  Orders.StartCheckout — no transaction spans these steps
  1 ══► Inventory.BeginCheckout(holdId, orderId, fanId)
          hold → InCheckout, expiry pushed out to a backstop (~35 min)
          ◄ seats + price categories  |  HoldNotActive → 409
  2 tx(orders): order Pending, prices from the local price list,
          deadline = now + 10 min, schedule CheckoutDeadline(orderId)
  3 ══► Payments.CreateCheckout(orderId, amount, providerExpiry = now + 30 min)
          tx: attempt Created → provider call (idempotency key = orderId) → tx: store session
  ◄─ {orderId, redirectUrl}

fan pays on the provider's page ─► redirected to /orders/{id}, which polls (the redirect proves nothing)
provider ─► WebhookIngress: check signature → store in payments inbox (unique event id) → 200
Workers · Payments: attempt Succeeded (amount checked) → outbox PaymentSucceeded
Workers · Orders:   ══► Inventory.ConfirmHold(holdId, orderId)          safe to repeat
                      Confirmed → order Confirmed → outbox OrderConfirmed
                      SeatsLost → order Refunding ══► Payments.RequestRefund
                                  → outbox OrderCancelled(reason)
```

| Crash after… | Recovery |
|---|---|
| `BeginCheckout` | The hold is `InCheckout` with no order; Inventory's backstop expiry releases it ([0010](0010-hold-lifetimes-and-payment-expiry.md)) |
| The order is created | The `CheckoutDeadline` timer cancels the payment and releases the hold |
| The provider call, before the session is stored | Retrying with the same idempotency key returns the same session |
| `PaymentSucceeded` is published, before `ConfirmHold` | The message is redelivered; `ConfirmHold` is safe to repeat |
| A payment is taken but no order exists (e.g. after a point-in-time restore) | Reconciliation finds it with `ListPaymentsAsync` and refunds it |

Rules that make this work:

- An `Idempotency-Key` header on `POST /holds` and `POST /orders`; one order per hold
  (unique constraint); the order ID is the provider's idempotency key.
- Every message handler is safe to repeat (inbox, [0014](0014-outbox-inbox-scheduler-on-postgresql.md)).
- `IPaymentProvider` gains two methods:
  - `CancelPaymentAsync`: for Stripe, expire the Checkout Session; returns `Cancelled` or
    `AlreadyCompleted`.
  - `ListPaymentsAsync(from, to)`: lets reconciliation find payments that have no order.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Merge Inventory and Orders into one "Sales" module | Hides the hardest boundary in the project; removes Inventory's own scaling path |
| Allow transactions across schemas inside the monolith | Breaks the extraction path and defeats the per-module database logins |

## Consequences

- [payments.md](../payments.md) is amended to this flow.
- Each crash point gets an integration test, driven by LocalPay scenarios.
- The redirect back from the provider never counts as proof of payment; only the webhook (or
  reconciliation) does.
