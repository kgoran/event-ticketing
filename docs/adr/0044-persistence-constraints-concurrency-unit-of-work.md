# 0044. Persistence rules: named constraints, optimistic concurrency, one unit of work per command

- **Status:** Agreed
- **Date:** 2026-10-19
- **Related:** [0011](0011-seat-inventory-concurrency.md) · [0014](0014-outbox-inbox-scheduler-on-postgresql.md) · [0031](0031-data-access-ef-core.md) · [0033](0033-command-pipeline.md) · design-decisions §14 (I1-d, I1-h, P1-f, O1-f)

## Context

[0031](0031-data-access-ef-core.md) chose EF Core, and [0011](0011-seat-inventory-concurrency.md)
optimistic concurrency (`xmin`) for everything outside the claim. Building M1a showed three
gaps: races that only a database constraint can catch, a lost update in a coordinator, and
hand-written queries that return rows.

## Decision

- **Named constraints as business outcomes.** A module names the unique constraints whose
  violation is an expected outcome and the error each stands for (e.g. one current hold per
  fan → `hold.active_hold_exists`). When saving violates one, the transaction runner rolls back
  and returns that error, so a race the handler's own check can't see still ends as a clean 409.
- **Optimistic concurrency on aggregates that several flows change**: orders, holds, payment
  attempts and refunds carry PostgreSQL's `xmin` as a row version. A conflicting save fails
  instead of overwriting; the runner turns it into `ConcurrencyConflictException`, which is safe
  to retry. Messages are retried by the outbox relay and the scheduler; a coordinator may retry
  the command itself.
- **One unit of work per command.** The transaction runner clears tracked entities after each
  commit, so the next command in the same scope loads current state instead of a stale copy.
- **Hand-written SQL that returns rows** goes through `ModuleSql` (shared), on the module's
  connection and inside its transaction (the claim needs `UPDATE … RETURNING`).

## Alternatives considered

| Alternative | Why not |
|---|---|
| Catching constraint violations in each handler | Handlers don't save; the runner does, after them |
| Mapping every unique violation to 409 | Most are bugs and should fail loudly |
| Pessimistic locks (`SELECT … FOR UPDATE`) on aggregates | Locks across application round trips ([0011](0011-seat-inventory-concurrency.md), alternative A) |
| Retrying conflicts inside the runner | Hides retries; requests and messages retry at their own level |

## Consequences

- A test caught the lost update this prevents: a coordinator's stale copy of a payment attempt
  overwrote the success a webhook had just recorded.
- New aggregates that several flows change should get the row version too.
