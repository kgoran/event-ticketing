# 0032. Patterns inside modules

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0006](0006-ticketing-modules.md) · [0009](0009-checkout-without-shared-transaction.md) · [0011](0011-seat-inventory-concurrency.md) · [0026](0026-module-project-layout.md) · [0031](0031-data-access-ef-core.md) · design-decisions §3, §4

## Context

Modules differ: some have real business rules, some are little more than CRUD
([0006](0006-ticketing-modules.md)). The team knows Clean Architecture; the aim is to keep
what pays off and drop the ceremony that doesn't.

## Decision

**From Clean Architecture we keep:**

- the dependency rule (the domain has no framework references);
- use cases as explicit command handlers;
- infrastructure behind interfaces;
- repositories only for aggregate roots.

**We drop:**

- repositories for reads (queries go straight to EF Core projections or SQL);
- mapping at every layer boundary (map once, at the edge);
- abstracting EF Core away;
- a mediator for everything.

**Structure:** the Application code (or the single project of a thin module) is organized by
feature folders, e.g. `Orders/PlaceOrder/` holds the command, handler and validator.

**Patterns:**

- **Value objects:** `Money` as integer minor units plus currency, with arithmetic only within
  one currency; strongly typed IDs (`EventId`, `OrderId`, …).
- **Small aggregates** that reference each other by ID; optimistic concurrency with `xmin`;
  pessimistic locks only where contention requires them. The hold path's single-statement claim
  is the agreed exception ([0011](0011-seat-inventory-concurrency.md)).
- **Explicit state machines** for lifecycles: order, payment, hold, ticket.
- **Process manager** for multi-step flows, with progress persisted and failed steps undone by
  compensating actions; checkout is the main example
  ([0009](0009-checkout-without-shared-transaction.md)).
- **Pluggable policies** chosen by configuration, never `if (x == ...)` in code (e.g. hold
  durations, the check-in policy for tickets not on the door list).
- **Result types** for expected business outcomes (sold out, hold expired); exceptions only for
  the exceptional.
- **Lightweight CQRS inside a module:** writes go through aggregates, reads are projections or
  cache-backed read models.
- **Specification pattern** only where it earns its place.

Modules with a full domain model use all of this; thin modules use endpoint → handler → query
slices ([0026](0026-module-project-layout.md)).

## Alternatives considered

| Alternative | Why not |
|---|---|
| Full Clean Architecture ceremony everywhere (read repositories, mapping layers, mediator) | Cost without benefit in thin modules and on read paths |
| Transaction scripts everywhere | Loses the domain-modelling lessons exactly where the rules are hard |

## Consequences

- Code review checks these patterns; architecture tests enforce the dependency rule
  ([0034](0034-test-stack.md)).
- Two module styles exist side by side, by design.
