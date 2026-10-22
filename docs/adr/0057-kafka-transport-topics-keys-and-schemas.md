# 0057. The Kafka transport: brokers, topics, keys, schemas and dead letters (M4)

- **Status:** Agreed (decided overnight for the owner's review, D76–D84)
- **Date:** 2026-10-22
- **Related:** [0003](0003-core-platform-technologies.md) · [0014](0014-outbox-inbox-scheduler-on-postgresql.md) · [0016](0016-teaching-milestones.md) · [0019](0019-autoscaling-signals.md) · [0055](0055-high-availability-in-the-lab.md) · [M4 plan](../milestones/m4.md)

## Context

ADR-0014 built the outbox, the inbox and the scheduler on PostgreSQL in M1, delivering events
in-process, and left Kafka for M4: partitions and keys, ordering, dead letters and replay, schema
versioning. ADR-0003 chose the Kafka protocol with Redpanda locally, and Protobuf schemas. What
was left open: how many brokers, which topics, what the key is, how the schemas are written, and
what happens to a message that can't be handled.

## Decision

- **Brokers (D76):** Redpanda, three brokers in the lab, one per zone, replication factor 3,
  producers with `acks=all` and at least two replicas in sync; one broker under Aspire. Redpanda
  Console shows topics, partitions and consumer groups.
- **Client (D77):** Confluent.Kafka; no health checks in readiness (D74).
- **Topics and keys (D78):** a topic per publishing module, `eventticketing.<module>`, 12
  partitions, retained 7 days, compressed with zstd. The key is the entity whose events must stay
  in order: the order for Orders and Payments, the event for Catalog, the order for Ticketing. The
  event's name and version, its message ID and its trace travel in headers.
- **Schemas (D79):** Protobuf, code-first with protobuf-net: the contracts stay C# records, each
  property with an explicit field number. The `.proto` files are generated from them and committed
  next to each contracts project; a test fails when they are stale. A field's number is never
  reused; a change that a consumer can't read gets a new version of the event, as before.
- **Size (D80):** a message stays whole up to 16 MB (the catalog topic allows it):
  `EventPublished` for the 50,000-place load-test venue is about 1.4 MB.
- **Consumers (D81):** a consumer group per consuming module. Messages of a partition are handled
  in order, each through the inbox in the module's transaction; the offset is committed after.
  A failing message is retried in place for about a minute, then written to the group's
  dead-letter topic with its error, and the partition goes on. `lab dlq` lists and replays.
- **Transport by configuration (D82):** Kafka under Aspire and in the lab; in-process in the
  product's tests and where no broker is configured. The outbox and the scheduler stay in
  PostgreSQL either way.
- **Workers split by configuration (D83):** a deployment runs the consumers of the modules it
  lists, and the relay and scheduler if told; the lab runs `workers` and `workers-notifications`.
- **Replay (D84):** a new consumer group starts from the earliest offset, so a module added later
  (Reporting, or CheckIn after M3's lesson 10) builds its state from what is retained.

## Alternatives considered

| Alternative | Why not |
|---|---|
| One broker in the lab | No replication to see; the zone drill couldn't take a broker |
| Apache Kafka in KRaft mode | The same protocol, heavier brokers |
| A topic per event type | An order's events on different topics lose their order |
| `.proto` first with Google.Protobuf | Generated mutable classes would replace the records every handler and test uses |
| JSON payloads | ADR-0003 chose Protobuf; its field numbers make evolution explicit |
| The claim check for big events | Right for unbounded payloads; ours are bounded under 16 MB |
| Retry topics with delays | More topics and more ordering surprises than a minute in place |
| Blocking a partition until a poison message is fixed | One bad message would stop every other order on the partition |

## Consequences

- Events are ordered per key, not across topics: consumers keep tolerating cross-module order.
- The relay's "processed" means "acknowledged by the brokers"; the inbox still drops duplicates.
- Workers' backlog becomes consumer lag, which KEDA can scale on in M5 (ADR-0019).
- The lab needs about 3 GB more for the brokers.
