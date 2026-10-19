# 0043. How a checkout ends: order states, the deadline, refunds and retries

- **Status:** Agreed
- **Date:** 2026-10-19
- **Related:** [0007](0007-module-communication.md) · [0009](0009-checkout-without-shared-transaction.md) · [0010](0010-hold-lifetimes-and-payment-expiry.md) · [0041](0041-holds-sales-window-release-and-sweeper.md) · [0042](0042-payment-provider-contract-and-webhooks.md) · [0045](0045-recurring-messages-and-coordinating-handlers.md) · design-decisions §14 (O1-a, c…e, O2-a…d, Q2-a)

## Context

[0009](0009-checkout-without-shared-transaction.md) coordinates checkout without a shared
transaction and lists the crash points; [0010](0010-hold-lifetimes-and-payment-expiry.md) says
"cancel the payment first, release the seats second". Building Orders (M1a O1, O2) needed the
order's states and every way a checkout can end.

## Decision

**Order states**

```
Pending ──paid, seats sold──────────────► Confirmed
   │    └─paid, seats taken meanwhile──► Refunding ──► Refunded | RefundFailed
   ├──deadline: payment cancelled, seats released──► Expired ─┐
   └──declined: payment cancelled, seats released──► Failed ──┴─paid after all──► Refunding
```

Every transition is safe to repeat. Expired and Failed checkouts publish no event;
`OrderConfirmed` and `OrderRefunded` are published.

**The deadline** (10 minutes, a policy) is scheduled in the order's own transaction. At the
deadline a coordinating handler ([0045](0045-recurring-messages-and-coordinating-handlers.md)):

| Payments says | Then |
|---|---|
| Cancelled | Release the hold, then mark the order Expired |
| AlreadyCompleted | Wait for the webhook; if it hasn't confirmed the order after 2 minutes, ask the provider |
| ProviderUnavailable | Try again after 30 s (at worst the backstop gives the seats back) |

- A **declined payment** closes the checkout at once through the same steps (order Failed).
- **Paid but no seats**: either the seats were lost after the hold expired, or money arrived for
  a checkout that had already closed. Both go Refunding, and a coordinating message asks
  Payments for the refund, retrying while the provider is unreachable.
- **The price list** keeps, per category, the price Catalog set last (the message's time), so
  `EventPublished` and `PricesChanged` can arrive in either order. Placed orders keep their prices.

**Retries from the fan**

- `POST /orders` for a hold that already has a Pending order **resumes it**: same order, same
  payment page (Payments' idempotency key finds the provider's session), with or without an
  Idempotency-Key, and when two requests race. Another fan gets 409; a closed order
  `checkout.order_closed`.
- `GET /orders/{id}` is what the return page polls: status, deadline, seats and refund reason,
  `Cache-Control: no-store`.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Waiting for the deadline after a decline | The fan couldn't start again for minutes |
| Refunding inside the `PaymentSucceeded` consumer | A provider call inside a transaction |
| Publishing an event for expired checkouts | Nobody outside Orders cares; `OrderCancelled` is for sold orders (M4) |
| Rejecting a repeated `POST /orders` | A fan whose first attempt failed half-way would have to wait for the deadline |

## Consequences

- Every crash point of [0009](0009-checkout-without-shared-transaction.md) that applies in M1a
  recovers, and has a test (M1a Q2).
- RefundFailed orders need a person; the back office will show them (M1b/M4).
