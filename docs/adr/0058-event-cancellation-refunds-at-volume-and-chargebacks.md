# 0058. Event cancellation, refunds at the provider's rate, and chargebacks (M4)

- **Status:** Agreed (decided overnight for the owner's review, D85–D89)
- **Date:** 2026-10-22
- **Related:** [0009](0009-checkout-without-shared-transaction.md) · [0015](0015-performance-targets.md) (T6, T9) · [0016](0016-teaching-milestones.md) · [0056](0056-check-in-devices-scanner-and-revocation.md) · [payments](../payments.md) · [architecture §4.7](../architecture/overview.md#47-event-cancellation-with-refunds) · [M4 plan](../milestones/m4.md)

## Context

M1 refunds one order when its seats were lost. M4 needs the same at volume: an organizer cancels
an event with tens of thousands of orders, and each must be refunded exactly once, as fast as the
provider allows, and every ticket must stop letting anyone in. A chargeback takes money back
without us, and must revoke the order's tickets. T6 asks how fast tickets follow a burst of
payments.

## Decision

- **Cancellation (D85)** as the architecture's §4.7: Catalog publishes `EventCancelled`;
  Inventory stops holds; Ticketing revokes every ticket in pages of 500; Orders runs one
  cancellation per event, which cancels open checkouts and moves confirmed orders to Refunding in
  pages, each refunded through the existing path (one refund per payment, safe to repeat);
  Notifications emails the buyers. BackOfficeApi shows the progress.
- **The provider's rate (D86):** Payments takes a token from a limiter in Valkey, shared by every
  Workers instance, before each provider call; a 429 waits its Retry-After. Without Valkey, each
  instance takes its share. LocalPay gets a configurable limit; Stripe's is 25/s.
- **T9 (D87)** is checked at lab scale in M4 (about 1,500 orders), and at 50,000 in M5 with the
  load-test data set.
- **Chargebacks (D88):** a dispute webhook makes Payments publish `DisputeOpened`; Orders revokes
  the order, Ticketing its tickets. Nothing is refunded: the provider took the money.
- **T6 (D89):** `OrderConfirmed` and `TicketsIssued` carry `PaidAt`; histograms measure payment to
  tickets and to email; a burst is LocalPay sending held webhooks together.

## Alternatives considered

| Alternative | Why not |
|---|---|
| A coordinator calling every module, as checkout does | Right for one order; for 50,000 a tracker and modules reacting on their own scale better |
| Each instance at the full rate limit | N instances would go N times over it |
| A single refund worker | No failover, and it can't move faster than one instance anyway |
| A data set inserted with SQL for T9 now | Brittle across five modules' schemas, before M5's data set exists |
| A chargeback as a refund | We would pay the money back twice |

## Consequences

- A cancelled event's door lists refuse its tickets within seconds; refunds take orders ÷ rate.
- The refund path is shared by single refunds, cancellations and back-office refunds.
- `OrderConfirmed` v1 gains a field: the first schema change made under D79's rules.
