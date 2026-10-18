# 0033. Our own command pipeline with decorators; no mediator

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0007](0007-module-communication.md) · [0009](0009-checkout-without-shared-transaction.md) · [0014](0014-outbox-inbox-scheduler-on-postgresql.md) · [0023](0023-single-tenant-organizer-scoped-data.md) · [0032](0032-patterns-inside-modules.md) · design-decisions §5

## Context

Every command needs the same cross-cutting steps: context, authorization, validation,
idempotency, a transaction with the outbox, audit, tracing. MediatR became commercial in 2025,
and a mediator between modules hides dependencies (design-decisions §6).

## Decision

- A small `ICommandHandler<TCommand, TResult>` interface of our own.
- Decorators applied with Scrutor, in this order:

  ```
  tenant/context → authorization → validation → idempotency → transaction + outbox → audit → tracing/metrics
  ```

  - **tenant/context:** the organizer context ([0023](0023-single-tenant-organizer-scoped-data.md)).
  - **validation:** FluentValidation.
  - **idempotency:** the `Idempotency-Key` header, or the message ID for messages
    ([0009](0009-checkout-without-shared-transaction.md)).
  - **transaction + outbox:** state change and outgoing events commit together
    ([0014](0014-outbox-inbox-scheduler-on-postgresql.md)).
  - **audit:** who did what, and why, recorded per command (not just row changes).
- The same pipeline wraps commands triggered by HTTP requests and by consumed messages.
- Endpoints and consumers resolve handlers directly from dependency injection; there is no
  mediator. Calls between modules go through module APIs ([0007](0007-module-communication.md)).
- Queries don't go through the command pipeline; they read projections directly
  ([0032](0032-patterns-inside-modules.md)).

## Alternatives considered

| Alternative | Why not |
|---|---|
| MediatR | Commercial since 2025; hides dependencies between modules |
| Wolverine handlers | A complete handler and messaging model of its own, overlapping with ours |
| Endpoint filters only | Don't cover commands triggered by consumed messages in Workers |

## Consequences

- One place for cross-cutting concerns; adding a concern means adding one decorator.
- The decorator order is covered by a test.

## Updates

- **2026-10-18 (built in F4):** a command that runs another command of the **same** module joins
  its transaction; a command of a **different** module gets its own transaction and commits
  independently ([0009](0009-checkout-without-shared-transaction.md)); re-entering a module that is
  already open further out is refused. A coordinating command that calls other modules or an
  external service (e.g. `StartCheckout`) must not hold a transaction open while doing so; a way to
  mark such commands as non-transactional arrives with backlog item O1.
