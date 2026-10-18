# 0024. Domain and UI scope for M1–M6

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0006](0006-ticketing-modules.md) · [0010](0010-hold-lifetimes-and-payment-expiry.md) · [0011](0011-seat-inventory-concurrency.md) · [0013](0013-offline-first-check-in.md) · design-decisions §13 (Q8, Q11)

## Context

The brief says: keep the domain simple and the engineering concerns rich. The architecture
pass surfaced several requirements that were missing or unclear.

## Decision

**In scope**

- Fans need an account to buy (required for per-fan limits and the waiting room).
- A per-fan ticket limit per event (default 8), at most one active hold per fan per event, and
  a maximum number of seats per hold.
- Reserved seating and standing areas (both as sellable rows, [0011](0011-seat-inventory-concurrency.md)).
- Event cancellation with refunds; refunding a single order from the back office (same path).
- Chargebacks invalidate tickets.
- Check-in at the door, offline-first ([0013](0013-offline-first-check-in.md)).

**Out of scope**

| Item | Reason |
|---|---|
| Moving an event to a new date; cancelling part of an event | Rescheduling happens more often than cancellation in practice, but it adds a refund window and re-notification flows without teaching anything new |
| Ticket transfer and resale | Ownership transfer and code re-issue are a separate domain |
| Pricing rules and promotions | Domain complexity without engineering value |
| Fees and VAT | Same |
| Payouts and settlement to organizers | Would need a ledger |
| Organizer API and outbound webhooks | Optional in the brief; integration events keep it addable later |
| Several currencies within one event | One currency per event |

**Rules**

- Times are stored in UTC plus the venue's IANA time zone; on-sale times are instants.
- Card and wallet payments only ([0010](0010-hold-lifetimes-and-payment-expiry.md)).

**UI scope**

- A minimal fan UI: event page, seat selection, queue page, checkout redirect, my tickets.
- A minimal Blazor back office.
- The door scanner as an offline-capable web app (PWA).
- The fan site's technology stays as in design-decisions §9 (Proposed: Next.js).

## Alternatives considered

Each "out" item above was considered and left out for the reason given.

## Consequences

- Load tests and the Scaling Lab hit the APIs directly; the fan UI only needs to demonstrate
  the flows.
- Adding an out-of-scope item later needs its own ADR.
