# 0042. The payment provider contract and webhook handling

- **Status:** Agreed
- **Date:** 2026-10-19
- **Related:** [0004](0004-payment-providers-stripe-and-localpay.md) · [0009](0009-checkout-without-shared-transaction.md) · [0010](0010-hold-lifetimes-and-payment-expiry.md) · [0044](0044-persistence-constraints-concurrency-unit-of-work.md) · [payments.md](../payments.md) · design-decisions §14 (P1-a…e, P2-b…d, S2-d)

## Context

[payments.md](../payments.md) lists `IPaymentProvider`'s operations, and
[0009](0009-checkout-without-shared-transaction.md) the checkout flow. Building Payments (M1a P1,
P2) needed the exact outcomes, how webhooks find their payment, and what Payments offers Orders.

## Decision

**Provider interface**

- `CancelPaymentAsync` answers **Cancelled** whenever nothing can be paid any more (expired,
  failed, unknown to the provider) and **AlreadyCompleted** only for a success. Orders needs one
  decision at the deadline: release the seats, or wait for the webhook.
- `GetStatusAsync` reports Open, Succeeded, Failed or Expired.
- `RefundAsync` takes our refund ID as the provider's idempotency key.
- `ListPaymentsAsync` exists; LocalPay supports it with reconciliation in M4.

**Payments' module API** (for Orders)

- `CreateCheckoutAsync`, `CancelCheckoutAsync` (Cancelled / AlreadyCompleted /
  ProviderUnavailable), `CheckPaymentStatusAsync` and `RequestRefundAsync`, all safe to repeat.
- News about a payment is applied in one place, from a webhook or a status check alike: the
  first report that changes the attempt publishes `PaymentSucceeded` or `PaymentFailed`; repeats
  publish nothing. Refunds work the same way with `RefundSucceeded` / `RefundFailed`.

**Webhooks**

- WebhookIngress verifies the signature, stores the webhook once and answers 200; a scheduled
  message due at once hands it to Workers.
- A duplicate, even one arriving at the same moment, is acknowledged with 200 so the provider
  stops retrying (the unique index maps to `webhook.duplicate`).
- A webhook finds its attempt by the provider's payment ID or, when it overtook the call that
  stores the session, by our reference (the order ID). Storing the session never undoes an
  outcome that arrived first.
- A webhook whose amount or currency differs from the attempt marks nothing paid: its
  processing throws, retries and dead-letters for a person to look at.

**Refunds (M1a)**

- One full refund per payment. A coordinator records it, asks the provider outside any
  transaction, and records the answer. The answer or the `refund.succeeded` webhook, whichever
  comes first, completes it. A refusal ends as `RefundFailed` with the provider's reason; only
  unreachability is retried.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Separate cancel outcomes for "failed" and "expired" | Orders does the same thing for both |
| Matching webhooks by payment ID only | A webhook that overtakes the session store would fail until retried |
| Answering duplicates with 409 | The provider would keep retrying a webhook we already have |
| Several partial refunds in M1a | Not needed until event cancellation and back-office refunds (M4) |

## Consequences

- Stripe's adapter must map its own outcomes onto these (an expired Checkout Session is
  Cancelled; a declined card leaves the session open).
- Dead-lettered webhooks need a person; the metrics in [0047](0047-one-trace-per-purchase-and-metric-tags.md)
  and the messaging metrics show them.
