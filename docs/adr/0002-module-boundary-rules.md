# 0002. Module boundary rules

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0001](0001-modular-monolith-with-several-hosts.md) · [0006](0006-ticketing-modules.md) · [0007](0007-module-communication.md) · [0009](0009-checkout-without-shared-transaction.md) · design-decisions §2

## Context

Boundaries that aren't enforced erode. These rules make every dependency between modules
visible and keep the extraction path from [0001](0001-modular-monolith-with-several-hosts.md)
open.

## Decision

| Rule | Detail |
|---|---|
| One module per business area | The ticketing module list is in [0006](0006-ticketing-modules.md) |
| Public contract, private implementation | Other modules reference only `X.Contracts` (module API and integration events); everything else is `internal` |
| One database schema per module | Own DbContext, own schema, own database login. Other modules' data is referenced by ID only: no cross-schema foreign keys, joins or transactions |
| Small, explicit module API for synchronous calls | Intention-named interfaces that return plain data objects, never entities. Kept to the minimum listed in [0007](0007-module-communication.md) |
| Events for "something happened" | Integration events are written to an outbox in the same transaction and published afterwards. Receivers record processed message IDs (inbox) to drop duplicates. Events are versioned public contracts ([0014](0014-outbox-inbox-scheduler-on-postgresql.md)) |
| Anti-corruption layer per external provider | Each provider adapter translates the provider's model into ours |

**No database transaction ever spans two modules.** Flows that touch several modules are
coordinated as described in [0009](0009-checkout-without-shared-transaction.md).

## Alternatives considered

| Alternative | Why not |
|---|---|
| Shared DbContext or shared schema | Hidden coupling through tables; makes moving one module to its own database server impossible ([0011](0011-seat-inventory-concurrency.md) relies on that option for Inventory) |
| A separate database server per module from day one | Operational cost now; schema-per-module keeps the option open |

## Consequences

- Data needed across modules is either carried in events or fetched through a module API.
  Reporting builds its own projections from events.
- One documented exception: the tooling's invariant checker reads across schemas with a
  read-only login that exists only in the lab environment
  ([0021](0021-product-tooling-platform-separation.md)).
