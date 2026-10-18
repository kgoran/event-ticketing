# 0014. Outbox, inbox and scheduler on PostgreSQL from M1; Kafka from M4

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0002](0002-module-boundary-rules.md) · [0003](0003-core-platform-technologies.md) · [0010](0010-hold-lifetimes-and-payment-expiry.md) · [0016](0016-teaching-milestones.md) · design-decisions §5

## Context

The brief introduced the outbox together with Kafka in milestone 4. But the payment flow
([0009](0009-checkout-without-shared-transaction.md)) can't be correct without reliable event
publication, and it is built in milestone 1.

## Decision

- **Outbox, from M1:** each module writes its integration events to an outbox table in its
  own schema, in the same transaction as the state change.
- **Inbox, from M1:** each consumer records processed message IDs in the same transaction as
  its own changes, and drops duplicates.
- **Scheduled messages, from M1:** delayed work (checkout deadlines, retries, the expiry
  sweeper, reconciliation) uses a scheduled-messages table.
- A relay claims rows with `FOR UPDATE SKIP LOCKED`, so any number of Workers instances can
  run it without leader election.
- **M1–M3:** the relay delivers events in-process to consumers in the Workers host.
- **M4:** the relay publishes to Kafka (Protobuf); consumers read from Kafka, still through
  the inbox. The message key is the entity whose order matters (order ID, event ID). The
  partition count is set generously up front, because adding partitions later moves keys.
- Whether we build this in-house or use Wolverine stays as in design-decisions §5 (Proposed:
  a small in-house implementation, or evaluate Wolverine). Either must meet this ADR.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Naive in-process events in M1, replaced by the outbox in M4 ("feel the pain") | Leaves a known money-losing bug in the code for three milestones; rework is an expensive way to teach |
| Quartz.NET for scheduled work | A second mechanism for what the outbox tables already provide |
| MassTransit | Commercial since v9 (design-decisions §5) |

## Consequences

- Correct from M1.
- M4 can concentrate on what's specific to Kafka: partitions and keys, ordering, dead-letter
  topics and replay, schema versioning, and scaling consumers on backlog with KEDA.
- Before Kafka, the outbox backlog is the Workers' autoscaling signal
  ([0019](0019-autoscaling-signals.md)).

## Updates

- **2026-10-18:** Built in-house. It is small, the team sees how it works, and it fits our own
  command pipeline ([0033](0033-command-pipeline.md)). Wolverine is not used.
- **2026-10-18 (built in F4):** every module schema has `outbox_messages`, `inbox_messages`,
  `scheduled_messages`, `idempotency_records` and `audit_records`, added by the shared
  `ModuleDbContext` base class. The relay and the scheduler claim rows with `FOR UPDATE SKIP LOCKED`
  plus a lease, retry with exponential backoff (capped at 5 minutes) and dead-letter after 10
  attempts. The inbox holds one row per message and consumer. A scheduled message's row is deleted
  in its handler's transaction. **Delivery order is not guaranteed in M1–M3**: consumers must
  tolerate events arriving out of order until Kafka keys give per-entity ordering in M4.
