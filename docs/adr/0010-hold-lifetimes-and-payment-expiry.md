# 0010. Hold lifetimes; cancel the payment before releasing the seats

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0009](0009-checkout-without-shared-transaction.md) · [0011](0011-seat-inventory-concurrency.md) · [0014](0014-outbox-inbox-scheduler-on-postgresql.md) · design-decisions §13 (Q5)

## Context

Checked against Stripe's documentation on 2026-10-18: a Checkout Session expires between 30
minutes and 24 hours after it is created (default 24 hours). It can be expired early through
the API, but only while its status is `open`; once it is complete, that call fails.

During an on-sale, seats held by someone who walked away must come back within minutes, not
half an hour.

## Decision

| Phase | Owner | How it works |
|---|---|---|
| Hold before checkout | Inventory | **Expiry is checked inside the claim itself:** when the next fan claims a seat whose `held_until < now()`, the seat counts as free, so it can be held again at the exact moment the old hold expires. A sweeper every ~2 s only tidies up and refreshes the availability snapshot |
| Checkout | Orders coordinator | At the deadline, Orders calls `Payments.CancelCheckout`. **Cancelled:** release the hold, mark the order Expired. **AlreadyCompleted:** wait for `PaymentSucceeded`. **Provider unreachable:** retry |
| Backstop | Inventory | Holds in checkout expire at the provider's session expiry plus a margin, even if Orders never acts |

Default durations. They are policies, configurable per event:

| Hold before checkout | Checkout deadline | Provider session | Backstop |
|---|---|---|---|
| 8 min | 10 min | 30 min (Stripe's minimum) | provider expiry + 5 min (~35 min) |

Rules:

- **Cancel the payment first, release the seats second.** Once the session is expired no
  payment can arrive, so the normal path never produces a late payment.
- All expiry decisions use the database clock (`now()`), never an application server's clock.
- The seat's own row records which hold owns it; that row is the source of truth.
- One small database-backed scheduler handles checkout deadlines, the sweeper,
  reconciliation and refunds. Jobs are claimed with `FOR UPDATE SKIP LOCKED`, so any number of
  Workers instances can run it without leader election
  ([0014](0014-outbox-inbox-scheduler-on-postgresql.md)).
- Card and wallet payments only. Methods that settle days later (SEPA Direct Debit, some bank
  redirects) would complete long after the hold.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Holds of 30 minutes or more | One abandoned checkout locks seats for half an hour during an on-sale |
| Authorize now, capture later (confirm seats first, then capture or cancel the authorization) | No refunds at all, but more provider calls and more LocalPay work. Good stretch goal for M4 |
| Release seats first, then cancel the payment | A payment can complete in between, forcing a refund |
| Quartz.NET instead of our own scheduler | A dependency for what the outbox mechanics already give us |

## Consequences

- The refund for "seats lost" (in [0009](0009-checkout-without-shared-transaction.md)) is a
  safety net, not a normal path.
- Worst case: if the provider is unreachable at the deadline, seats stay held until the
  backstop (~35 min).
- Target T3 is split into "claimable" and "shown as available"
  ([0015](0015-performance-targets.md)).
