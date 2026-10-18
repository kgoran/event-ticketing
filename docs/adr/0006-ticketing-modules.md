# 0006. The ten ticketing modules

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0002](0002-module-boundary-rules.md) · [0007](0007-module-communication.md) · [0024](0024-domain-scope-m1-m6.md) · design-decisions §2, §3

## Context

The brief listed nine candidate modules as a starting point. They needed confirming,
refining, and an owner for each piece of data.

## Decision

| Module | Owns (in its own schema) | Style |
|---|---|---|
| **Catalog** | venues, seat-map layouts, events, price categories, on-sale schedule, event lifecycle | thin slices, plus a lifecycle state machine |
| **Inventory** | one row per sellable place per event (a seat or a standing slot), holds, availability snapshots | full domain model; hand-written SQL on the hold path ([0011](0011-seat-inventory-concurrency.md)) |
| **Orders** | orders, the prices recorded on each order, the checkout coordinator, progress of event cancellations | full domain model |
| **Payments** | payment attempts, provider adapters, incoming webhooks, refunds, reconciliation, disputes | full domain model |
| **Ticketing** | tickets, signed QR codes, signing keys, PDFs, revocations | full domain model |
| **CheckIn** | door lists (manifests), scanner devices, scan log, duplicate entries | full domain model |
| **Notifications** | email templates, delivery log, email adapter | thin slices |
| **Organizers** | organizers, their members and roles | thin slices |
| **Reporting** | sales and check-in projections built from events | thin slices |
| **WaitingRoom** | queues, queue positions, admission rate, admission-token keys (in Valkey, no EF Core) | thin slices |

Changes from the brief's draft list:

- **Events is renamed Catalog.** In a codebase full of integration events, names like
  `Events.Contracts.EventCancelledEvent` would confuse people every day. The main class is
  still called `Event`.
- **Ticketing is split into Ticketing and CheckIn.** Issuing tickets happens once per order
  and must be strictly consistent. Check-in has different users (door staff and devices), its
  own host, a different load pattern (a burst at doors-open) and the opposite consistency
  model: it must keep working offline ([0013](0013-offline-first-check-in.md)).
- **Organizers stays, but thin.** It is where "which organizer owns this data" lives
  ([0023](0023-single-tenant-organizer-scoped-data.md)) and the natural home for an organizer
  API or for tenancy if either is ever added.
- **Identity is not a business module.** It is a platform component (an identity provider;
  the choice is still open).

Allowed dependencies (on other modules' contracts). The graph has no cycles:

| Module | Depends on |
|---|---|
| Catalog, Organizers, Payments | nothing |
| Inventory | Catalog |
| WaitingRoom | Catalog, Inventory |
| Orders | Catalog, Inventory, Payments |
| Ticketing | Catalog, Orders |
| CheckIn | Catalog, Ticketing |
| Notifications | Orders, Ticketing |
| Reporting | events from most modules |

## Alternatives considered

| Alternative | Why not |
|---|---|
| Keep Ticketing as one module | Fewer projects, but it hides the clearest "strict versus offline" contrast the project can teach |
| Fold Organizers into Catalog | Reasonable while single-tenant with no organizer API, but it would have to be pulled out again if either changes |
| Merge Inventory and Orders into one "Sales" module | Hides the hardest boundary in the project and removes Inventory's own scaling path |

## Consequences

- An architecture test enforces the dependency table.
- Under the Clean Architecture layout (design-decisions §3, Option A), ten modules come to
  about 40 projects. That is an input to the still-open layout question: consider four projects
  only for the modules with a full domain model.
- What is out of scope is listed in [0024](0024-domain-scope-m1-m6.md).
