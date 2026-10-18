# 0031. EF Core with Npgsql; hand-written SQL only where measured necessary

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0002](0002-module-boundary-rules.md) · [0008](0008-hosts.md) · [0011](0011-seat-inventory-concurrency.md) · [0019](0019-autoscaling-signals.md) · [0032](0032-patterns-inside-modules.md) · design-decisions §9

## Context

Each module owns a PostgreSQL schema ([0002](0002-module-boundary-rules.md)). Most data access
is ordinary; one path, the seat claim, is the hottest code in the system
([0011](0011-seat-inventory-concurrency.md)).

## Decision

- **EF Core with the Npgsql provider.** One DbContext per module, mapped to the module's own
  schema and connecting with the module's own database login.
- Optimistic concurrency uses PostgreSQL's `xmin` as the row version.
- Reads go straight to EF Core projections; there are no repositories for reads
  ([0032](0032-patterns-inside-modules.md)).
- **Hand-written SQL only where a measurement shows it's needed.** Today that is only the seat
  claim on the hold path ([0011](0011-seat-inventory-concurrency.md)).
- **EF Core migrations per module, applied by the Migrator host** ([0008](0008-hosts.md)),
  never at application startup. Additive changes run before a rollout, removals after it
  (expand/contract).
- Pooled connections are recycled (Npgsql `Connection Lifetime`) so new read replicas receive
  traffic ([0019](0019-autoscaling-signals.md)).

## Alternatives considered

| Alternative | Why not |
|---|---|
| Dapper everywhere | Much more SQL to write and no change tracking for aggregates |
| A generic repository over EF Core | Hides EF Core without adding anything (design-decisions §6) |
| Migrations at application startup | Several instances would race; no control over expand/contract ordering |

## Consequences

- Seat rows are written by two code paths (the claim SQL and EF Core); both need integration
  tests.
- The team learns expand/contract migration discipline in M2.
