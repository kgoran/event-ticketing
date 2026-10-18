# 0003. PostgreSQL, Kafka, Valkey and Kubernetes as the core platform

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0005](0005-no-paid-services-local-first.md) · [0014](0014-outbox-inbox-scheduler-on-postgresql.md) · [0019](0019-autoscaling-signals.md) · [0020](0020-local-kubernetes-on-docker-desktop.md) · design-decisions §9

## Context

The architecture relies on a relational system of record with replication, an event
transport with partitions and replay, a fast shared cache, and an orchestrator that can
autoscale. All of them must be free and run locally ([0005](0005-no-paid-services-local-first.md)).

## Decision

| Area | Choice | Role |
|---|---|---|
| Database | PostgreSQL, run by the CloudNativePG operator on Kubernetes | System of record. Primary + synchronous standby + read replicas. Read replicas never make decisions about inventory or money |
| Messaging | Kafka protocol; Redpanda in development and the lab; Protobuf schemas | Event transport from M4. Before that the outbox relay delivers in-process ([0014](0014-outbox-inbox-scheduler-on-postgresql.md)) |
| Cache | Valkey | Read caches, availability snapshots, sold-out flags, waiting-room queues. Never authoritative for inventory or money |
| Compute | Kubernetes, with KEDA for event-driven autoscaling | Lab and CI environments ([0020](0020-local-kubernetes-on-docker-desktop.md)); autoscaling ([0019](0019-autoscaling-signals.md)) |

The waiting-room queue lives only in Valkey. That is acceptable because queue positions are
neither inventory nor money: if they are lost, the on-sale pauses
([0012](0012-build-our-own-waiting-room.md)).

## Alternatives considered

| Alternative | Why not |
|---|---|
| RabbitMQ instead of Kafka | Simpler, but doesn't teach partitions, ordering by key and replay, which the brief asks for |
| Redis instead of Valkey | Redis left its BSD licence in 2024; Valkey is the BSD-licensed fork with the same protocol |
| Aurora PostgreSQL, ECS Fargate | Cloud-only; excluded by [0005](0005-no-paid-services-local-first.md) |

## Consequences

- The team needs working Kubernetes basics from M2.
- The lab is memory-hungry; sizing is in [0020](0020-local-kubernetes-on-docker-desktop.md).
- A cache is only ever an optimisation: every decision about seats or money reads the primary.
