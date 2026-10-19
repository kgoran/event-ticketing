# 0041. Holds: the sales window, one hold per fan, release, and the sweeper

- **Status:** Agreed
- **Date:** 2026-10-19
- **Related:** [0007](0007-module-communication.md) · [0009](0009-checkout-without-shared-transaction.md) · [0010](0010-hold-lifetimes-and-payment-expiry.md) · [0011](0011-seat-inventory-concurrency.md) · [0024](0024-domain-scope-m1-m6.md) · [0038](0038-catalog-venues-seat-maps-and-event-lifecycle.md) · [0044](0044-persistence-constraints-concurrency-unit-of-work.md) · [0045](0045-recurring-messages-and-coordinating-handlers.md) · design-decisions §14 (I1-a…g, I2-b…e)

## Context

[0011](0011-seat-inventory-concurrency.md) fixed the claim (one conditional statement, expiry
checked inside) and the limits; [0010](0010-hold-lifetimes-and-payment-expiry.md) the lifetimes
and a sweeper that "only tidies up". Building Inventory (M1a I1, I2) needed the details.

## Decision

**Claiming**

- The sales window is checked **inside** the claim statement (`EXISTS` on Inventory's copy of
  the window, against `now()`). Only when a claim fails does a second query tell
  `hold.not_on_sale` from `hold.seats_unavailable`, so a successful hold stays one statement.
- Inventory's window remembers when Catalog set the on-sale time and ignores older values:
  retried deliveries let `OnSaleScheduled` overtake `EventPublished`.
- Standing slots get random IDs; their rank is the slot number.
- A hold's expiry is the `held_until` its claim wrote: hold and seat rows agree, by the database clock.

**One hold per fan per event**

- The handler checks first, for a clear message; a partial unique index on (event, fan) for
  Active and InCheckout holds catches two requests at once ([0044](0044-persistence-constraints-concurrency-unit-of-work.md)
  turns that into the same 409).
- An Active hold whose time ran out is marked Expired lazily, when the fan places the next one.
- Fans release their own Active hold (`DELETE /holds/{id}`). A hold in checkout belongs to its
  order: only the order releases it, through Inventory's module API.

**Module API** (for Orders, [0007](0007-module-communication.md))

- `BeginCheckout` takes the fan explicitly; a module API designed as if remote doesn't read the
  caller's HTTP context. At checkout, hold and seats move to the same backstop expiry.
- `ConfirmHold`, `BeginCheckout` and `ReleaseHold` are safe to repeat. `ReleaseHold` answers
  Released (also when repeated, expired or lost) or AlreadyConfirmed; a hold that isn't the
  order's is a bug, so the call throws and the message retries, then dead-letters.

**Sweeper and availability**

- The sweeper runs every 2 s as a recurring scheduled message ([0045](0045-recurring-messages-and-coordinating-handlers.md)).
  It shows expired seats as Available **but keeps their hold ID**, so a late confirmation still
  sells seats nobody took, exactly as lazy expiry does. It marks lapsed holds Expired, including
  holds in checkout past the backstop. Batches of 1,000, `SKIP LOCKED`, partial indexes.
- `GET /events/{id}/availability` gives free and total places per section and the free seat IDs
  (standing slots are counted, not listed), by the database clock. It is advice; the claim decides.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Checking the window before the claim | A second round trip on every hold, at the hottest moment |
| Unique index only, without the check | Correct, but the fan gets a constraint error instead of a message |
| No release endpoint | With one hold per fan, changing seats would mean waiting for expiry |
| A sweeper that clears the hold ID | A late payment would lose seats nobody took: sweeping would change who can buy a seat |

## Consequences

- At the moment a sweep runs, a claim on a seat it's touching skips that seat and may answer
  409 needlessly ([0011](0011-seat-inventory-concurrency.md)'s trade-off); the fan retries.
  Module tests run the sweeper only on demand for this reason.
- Holds that expired before checkout stay Active in the table until the fan's next hold or the
  next sweep; nothing depends on that column being current.
