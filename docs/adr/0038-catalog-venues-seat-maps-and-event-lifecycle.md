# 0038. Catalog: venues, seat maps and the event lifecycle

- **Status:** Agreed
- **Date:** 2026-10-19
- **Related:** [0006](0006-ticketing-modules.md) · [0007](0007-module-communication.md) · [0010](0010-hold-lifetimes-and-payment-expiry.md) · [0011](0011-seat-inventory-concurrency.md) · [0023](0023-single-tenant-organizer-scoped-data.md) · [0024](0024-domain-scope-m1-m6.md) · design-decisions §14 (C1-a…h, C2-b)

## Context

Catalog owns venues, seat maps, events, prices and the on-sale schedule ([0006](0006-ticketing-modules.md)).
Building it (M1a C1) needed answers the earlier ADRs didn't give: who owns a venue, what an
import looks like, how seats get their price, and whether "on sale" is a state.

## Decision

**Venues**

- A venue belongs to an organizer and is **never edited** once imported. A new layout is a new
  import, so an event on sale can't change under its fans.
- The import lists sections: seated ones as rows (label, seat count, first number), standing
  areas as a capacity. Grid positions and "best available" ranks are **derived** (front rows
  first, then the seats nearest the middle).
- A venue seat's ID is that seat's identity in every event at the venue. Inventory's rows and
  the seat-map geometry join on it in the client ([0007](0007-module-communication.md) rule 4).
- Events copy the venue's name and time zone when created; the copy can't go stale.
- Time zones are validated as IANA IDs with **NodaTime**'s bundled TZDB, because the repository
  runs with `InvariantGlobalization`.

**Prices**

- Price categories are assigned by rules: one per section, overridden per row. Every place
  must end up in exactly one category. The rules are resolved into `EventPublished` at publish.

**Lifecycle**

```
Draft ──Publish──► Published            (Cancelled joins later, with EventCancelled)
  ScheduleOnSale, ChangePrices: allowed in both; announced only once published
```

- **"On sale" is a time window, [OnSaleAt, StartsAt), not a stored state.** Inventory enforces
  it against the database clock ([0041](0041-holds-sales-window-release-and-sweeper.md)).
- A time in the past means "open sales now". Once sales have started, the time can't move.
- `PricesChanged` carries the full price list; `OnSaleScheduled` is sent only for published
  events (a Draft's schedule travels in `EventPublished`).

## Alternatives considered

| Alternative | Why not |
|---|---|
| Platform-wide venues managed by admins | Venues are shared in real life, but nothing in M1a needs it, and organizer ownership keeps authorization one rule |
| Editable venue layouts | Would change events already on sale |
| Explicit coordinates per seat in the import | Much bigger files; nothing more to learn |
| A stored `OnSale` state flipped by a scheduled job | Lags behind the clock and gives two answers to "is it on sale?" |
| `TimeZoneInfo` | Can't resolve IANA IDs on Windows with `InvariantGlobalization`, and depends on tzdata in Linux images |

## Consequences

- Seat IDs are stable across events at a venue; tickets, check-in and reporting can rely on it.
- A changed venue means re-importing it and creating new events there.
- Inventory needs Catalog's on-sale time; it keeps its own copy from the events above.
