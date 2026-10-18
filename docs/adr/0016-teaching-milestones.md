# 0016. Milestones mapped to modules, flows and targets

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0014](0014-outbox-inbox-scheduler-on-postgresql.md) · [0015](0015-performance-targets.md) · [0017](0017-scaling-lab-and-load-generator.md) · project-brief "Teaching milestones"

## Context

The brief proposes six teaching milestones. Each needs to say which modules, flows and hosts
it delivers, and which targets it proves.

## Decision

| Milestone | Modules | Flows working end to end | Hosts | Targets checked | Scaling Lab adds |
|---|---|---|---|---|---|
| **1. Modular monolith, CI, tests** | Catalog (minimal), Inventory, Orders, Payments (LocalPay first, then Stripe), Ticketing, Notifications, Organizers (thin) | browse (no caching yet), hold, checkout and payment, ticket issuance, hold expiry | PublicApi, BackOfficeApi, WebhookIngress, Workers (run by Aspire) | T2 in CI (parallel holds) | — |
| **2. Containers, deployment pipeline, migrations** | + Migrator | the same flows, in the lab cluster and in CI | + Migrator | T10 (deploy) | load generator and slider; request, error and latency charts; pod list |
| **3. High availability, failover drill** | + CheckIn; Valkey caching | browse through replicas and cache, check-in at the door, holds during a failover | + CheckInApi | T8, T10 (failover) | database panel: primary, replicas, replication delay |
| **4. Async: Kafka, duplicates** | + Reporting; refunds, reconciliation, disputes; event-cancellation process | event cancellation with refunds, ticket issuance at volume, chargebacks | Workers split by configuration | T6, T9 | Workers backlog panel, payment-burst preset |
| **5. Scaling, load tests, autoscaling** | + WaitingRoom; hold-path tuning | an on-sale of one hot event through the waiting room | + WaitingRoom; PublicApi split by configuration | T1, T3, T4, T5, T7, T11 | capacity settings, autoscaling, scaling timeline, presets |
| **6. Disaster recovery, runbooks** | reconciliation of payments with no order | every flow, after a restore | — | restore drill: payments lost in the data-loss window are rebuilt from the provider's records | — |

Also part of the plan:

- **Outbox, inbox and scheduler from M1**, on PostgreSQL; Kafka becomes the transport in M4
  ([0014](0014-outbox-inbox-scheduler-on-postgresql.md)).
- **Observability in every milestone**, not just one: basic OpenTelemetry in M1, service
  dashboards in M3, business alerts (e.g. "paid, but no tickets after 5 minutes") in M5 and M6.
- **"No deploys during an on-sale" becomes a pipeline check in M2.** The check reads the
  on-sale schedule from Catalog and blocks the deploy.
- **CheckIn lands in M3**, because it is about staying available when the network fails.
- **M1 is by far the largest milestone.** For a small team, split it into M1a (Catalog,
  Inventory, Orders, LocalPay) and M1b (Stripe, Ticketing, Notifications). This depends on team
  size, which is still open (design-decisions §13, Q10).

## Alternatives considered

| Alternative | Why not |
|---|---|
| Keep the brief's list without mapping | Nobody could tell when a flow or target is due |
| Introduce the outbox only in M4 | See [0014](0014-outbox-inbox-scheduler-on-postgresql.md) |
| Build the Scaling Lab all at once in M5 | M2 (deploy under load) and M3 (failover drill) benefit from watching live |

## Consequences

- Every milestone ends with a demo of its flows and a check of its targets.
- M1 carries the most risk; it should start with one vertical slice (hold → checkout with
  LocalPay) before adding breadth.

## Updates

- **2026-10-18:** M1 is split into **M1a** (Catalog, Organizers, Inventory, Orders, Payments
  with LocalPay) and **M1b** (Stripe adapter, Ticketing, Notifications). Team size is still unknown; splitting is the safer
  default. Each milestone gets a time-box, agreed at its kickoff.
- **2026-10-18:** The Migrator is built in M1a and runs as an Aspire resource that the other
  hosts wait for (`WaitForCompletion`), because [0031](0031-data-access-ef-core.md) forbids
  migrations at application startup. M2 turns it into a Kubernetes Job and adds expand/contract.
- **2026-10-18:** Single refunds (the seats-lost case) are in M1a, so that T2's "every paid order
  ends Confirmed or Refunded" holds from M1; mass, rate-limited refunds stay in M4. Detailed
  plan: [M1a](../milestones/m1a.md).
