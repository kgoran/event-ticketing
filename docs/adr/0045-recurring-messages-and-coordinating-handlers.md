# 0045. Scheduler: recurring messages and coordinating handlers

- **Status:** Agreed
- **Date:** 2026-10-19
- **Related:** [0010](0010-hold-lifetimes-and-payment-expiry.md) · [0014](0014-outbox-inbox-scheduler-on-postgresql.md) · [0033](0033-command-pipeline.md) · [0041](0041-holds-sales-window-release-and-sweeper.md) · [0043](0043-how-a-checkout-ends.md) · design-decisions §14 (I2-a, O1-b, O2-b)

## Context

The scheduler ([0014](0014-outbox-inbox-scheduler-on-postgresql.md)) ran each scheduled message's
handler inside the module's transaction. Two needs came up in M1a: a job that repeats (the
sweeper, later reconciliation) without leader election, and handlers that call other modules
or a provider, which [0033](0033-command-pipeline.md) says must not hold a transaction open.

## Decision

**Recurring messages** (`AddRecurringMessage<T>(interval)`)

- Each has **exactly one pending occurrence**, however many Workers instances run.
- The scheduler starts it when none is pending: an insert under an advisory lock, checked about
  once a minute per module, which also restarts a job whose occurrence was dead-lettered.
- Each run schedules the next occurrence in the same transaction as its work.

**Coordinating handlers** (`INonTransactionalMessageHandler`)

- They run outside any transaction; each step is a command or a module API call with its own.
- The scheduled row is deleted after the handler finishes. A crash in between runs the handler
  again, so every step must be safe to repeat. Commands it runs get the message ID as their
  idempotency key, per command type.
- Used for the checkout deadline, the payment status check and refunds ([0043](0043-how-a-checkout-ends.md)).

## Alternatives considered

| Alternative | Why not |
|---|---|
| A hosted service with a timer in each Workers instance | Every instance would run the job; recurring work belongs to the scheduler ([0010](0010-hold-lifetimes-and-payment-expiry.md)) |
| Handlers rescheduling themselves | Every handler would need to remember to; a failure would end the chain silently |
| A fixed message ID for the single occurrence | The run deletes its own row in the same transaction that inserts the next one |
| Quartz.NET | Rejected in [0010](0010-hold-lifetimes-and-payment-expiry.md) |
| Provider calls inside the handler's transaction | Holds a connection and a transaction open across a network call |

## Consequences

- A test starts 20 instances at once and finds one occurrence; removing the advisory lock makes
  it fail.
- A coordinating handler's work isn't atomic with "done": that's the price of not holding a
  transaction, paid by making every step safe to repeat.
