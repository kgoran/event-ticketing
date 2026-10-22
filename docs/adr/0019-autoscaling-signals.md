# 0019. What scales on which signal

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0003](0003-core-platform-technologies.md) · [0008](0008-hosts.md) · [0014](0014-outbox-inbox-scheduler-on-postgresql.md) · [0018](0018-capacity-from-configuration-not-code.md) · design-decisions §8

## Context

Each host and each data store has a different reason to grow. Choosing the wrong signal
makes autoscaling slow, jumpy, or harmful.

## Decision

| Component | Scaling signal | Tool | Notes |
|---|---|---|---|
| PublicApi | utilization = requests in progress ÷ concurrency limit; target 70% | KEDA, reading Prometheus | at 70 requests/s and ~14 requests/s per instance at target: ⌈70 ÷ 14⌉ = 5 instances, so the class can predict the result before moving the slider |
| Workers | backlog: unsent outbox rows in M1–M3, Kafka consumer lag from M4 | KEDA PostgreSQL scaler, then KEDA Kafka scaler | scales down to zero when idle |
| WaitingRoom | CPU or request rate | standard Kubernetes autoscaler | |
| Read replicas | replica CPU | KEDA, targeting the CloudNativePG cluster | `kubectl scale` works on CloudNativePG clusters, so KEDA should be able to drive them; to confirm in a spike (fallback: a small lab controller that changes the instance count) |
| Database writer | **does not scale out** | — | levers: caching, connection pooling, admission control. Giving the writer more resources means a restart with switchover, i.e. a short write outage |
| Before an on-sale | a scheduled window | KEDA cron scaler | in the lab, the preset sets the window; generating it from Catalog's on-sale schedule is part of M5 |
| Kubernetes nodes | — | — | not shown locally; a full cluster leaves pods `Pending` |

Two scaling profiles, switched from the Scaling Lab:

| Profile | KEDA polling | Scale-down stabilization |
|---|---|---|
| Demo speed | every 5 s | 30 s |
| Realistic | KEDA and Kubernetes defaults (30 s) | Kubernetes default (5 min) |

Rules:

- **Every autoscaled component has a maximum instance count.** When a dependency slows down,
  utilization-based scaling adds instances and makes the dependency worse (lab preset 4).
- **Pooled connections must be recycled** (Npgsql `Connection Lifetime`), otherwise new read
  replicas receive no traffic.

## Alternatives considered

| Alternative | Why not |
|---|---|
| CPU for PublicApi | Most request time is spent waiting for the database, not using CPU; CPU stays low while requests queue. Used only in the lab's realistic mode |
| Request rate per instance for PublicApi | Ignores how expensive the requests are; utilization captures that |
| Autoscaling the database writer | Not possible for writes; it's the main "what does not scale out" lesson |

## Consequences

- KEDA and Prometheus are required in every Kubernetes environment.
- Part of "scaling is slow" is "measuring is slow" (scrape interval + rate window + autoscaler
  cycle); the lab shows this instead of hiding it.

## Updates

- **2026-10-21 (M2):** A third rule, learned in the lab: **the database's connection limit must
  cover the most instances times each instance's pool.** Every instance keeps its own connection
  pool; five PublicApi instances under load held 96 of PostgreSQL's default 100 connections, and a
  new instance couldn't connect. The lab caps pools at 20 and allows 300 connections, enough for
  every host at its maximum ([lab](../lab.md)). A connection pooler in front of PostgreSQL comes
  with the replicas in M3.
- **2026-10-21 (D36):** The second rule covers HTTP too. Inside Kubernetes a Service balances per
  connection, not per request, so a client that keeps its connections sends everything to the
  instances it reached first, and new instances get none of its traffic. Every service-to-service
  HttpClient of the product recycles its pooled connections every minute (ServiceDefaults).
  Traffic through the gateway is balanced per request already.
- **2026-10-21 (D40, D41):** Until Prometheus arrives in M3, PublicApi scales on **CPU** in the lab
  (KEDA's `cpu` trigger, through metrics-server), with the switch, limits and profiles of
  [0054](0054-publicapi-autoscaling-switch-in-the-lab.md). The demo-speed profile waits 30 s before
  scaling down, the realistic one 5 minutes. KEDA's polling interval doesn't apply to CPU:
  Kubernetes' autoscaler reads metrics-server every 15 s. In M3 the trigger becomes utilization
  as above.
- **2026-10-22 (M3, D56, D69):** PublicApi scales on **requests in progress**, from Prometheus:
  the seconds of request time per second over 30 s (Little's law), divided by **0.5 per
  instance**, measured in the lab ([lab](../lab.md#autoscaling)). Until M5 brings the concurrency
  limit, the target is in requests in progress, not a share of a limit. A new instance's warm-up
  is left out of the metrics. Measured: a step from 50 to 150 requests/s went from 1 to 3 ready
  instances in 47 s and settled at 4, with no failed request. Two more lessons for the rules:
  requests in progress include waiting for the database's locks, which grows with the total load
  and which instances can't remove (the maximum matters); and **a CPU limit below a core** makes
  .NET size its thread pool for one processor, which starved it for a minute on a burst, so the
  lab's instances have CPU requests and no CPU limits.
