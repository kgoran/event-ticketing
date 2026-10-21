# 0055. High availability in the lab: zones, the database, Valkey, observability, drills

- **Status:** Agreed (decided overnight for the owner's review, D46–D59, D65)
- **Date:** 2026-10-21
- **Related:** [0003](0003-core-platform-technologies.md) · [0011](0011-seat-inventory-concurrency.md) · [0015](0015-performance-targets.md) · [0018](0018-capacity-from-configuration-not-code.md) · [0019](0019-autoscaling-signals.md) · [0020](0020-local-kubernetes-on-docker-desktop.md) · [0025](0025-object-storage-and-secrets.md) · [0028](0028-gateway-and-browse-caching.md) · [0049](0049-fan-site-blazor-server-tokens-on-the-server.md) · [0053](0053-the-lab-cluster-addresses-identity-and-platform.md) · [M3 plan](../milestones/m3.md)

## Context

M2 runs every stateless host as several instances, but one of everything stateful: one node,
one database instance, no cache. M3's drill kills the primary under load (T10: under 60 s of
errors, no broken T2 invariant), and its browsing must not reach the primary (T4). The pieces
were named in earlier decisions; this one says how they fit together in the lab.

## Decision

- **Three nodes as zones (D47).** kind's control plane and two workers, all schedulable,
  labelled `zone-a`, `zone-b`, `zone-c`. Hosts with two or more instances spread over zones;
  WebhookIngress never has two instances on one node. Keycloak's memory is capped.
- **The database (D48).** CloudNativePG with three instances, one per zone: the primary, one
  synchronous standby, one asynchronous read replica. A commit waits for the standby (RPO 0 for a
  zone); CloudNativePG promotes the standby when the primary fails.
- **The pooler (D49).** CloudNativePG's `Pooler`: PgBouncer in transaction mode, two instances,
  in front of the primary. The product writes through it; its connections to PostgreSQL stay
  bounded however many instances run. Session state across transactions is not allowed.
- **Reads (D50).** Only Catalog's browsing reads from the replica, through a second, read-only
  connection string, with connections recycled. Everything that decides reads the primary.
- **Valkey (D51–D54).** One instance with append-only persistence, never authoritative: the browse
  output cache (ADR-0028), the availability snapshot rebuilt once a second per on-sale event, the
  sold-out flags, the fan site's token store and key ring. The fan site runs twice with sticky
  sessions.
- **Observability (D55, D56).** An OpenTelemetry Collector receives what the hosts send and passes
  metrics to Prometheus, logs to Loki and traces to Tempo; Grafana shows a dashboard per service,
  provisioned from `product/deploy/dashboards`. The Aspire Dashboard stays for Aspire only.
  PublicApi's autoscaler moves to requests in progress per instance (ADR-0054's planned change).
- **Toxiproxy (D57)** sits between the hosts and PgBouncer, with no delay until a drill adds one.
- **Invariants and drills (D58, D59).** `tooling/checks` checks T2's rules in SQL through a
  read-only login that exists only in the lab. `lab drill failover` deletes the primary's pod
  under load; `lab drill zone` stops a node. Each reports the error window and runs the checker.
- **Backups (D65)** go to SeaweedFS through CloudNativePG's Barman Cloud plugin (with
  cert-manager, which it needs): a daily base backup and the WAL, and one restore checked.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Two database instances | No replica to read from while the standby takes the writes' second copy |
| Every replica synchronous | One slow replica would slow every write |
| Valkey with Sentinel | Three more pods for data we may lose |
| kube-prometheus-stack | Heavier, and its alerting waits for M5 |
| Chaos Mesh for the drills | Another operator for two drills a script does |
| RustFS for backups | §13's fallback; SeaweedFS was agreed in ADR-0025 |

## Consequences

- The lab needs about 3–4 GB more; capping Keycloak and dropping the Aspire Dashboard pay for
  most of it.
- Code must not rely on database sessions (PgBouncer), and must say which reads may be stale.
- A deploy of the fan site no longer signs fans out.
- T10's second half and T4's shape become measurable in the lab.
