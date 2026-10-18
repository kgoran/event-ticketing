# 0005. No paid services; everything runs locally

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0012](0012-build-our-own-waiting-room.md) · [0015](0015-performance-targets.md) · [0020](0020-local-kubernetes-on-docker-desktop.md) · [0025](0025-object-storage-and-secrets.md) · design-decisions §9, §10, §13 (Q6)

## Context

There is no budget. Earlier proposals assumed AWS (Frankfurt primary, Ireland for disaster
recovery), Aurora, EKS, Karpenter and Cloudflare.

## Decision

- Only free and open-source components and free tiers. No paid service of any kind.
- Everything runs locally. The lab environment runs on Kubernetes inside Docker Desktop on the
  project owner's laptop ([0020](0020-local-kubernetes-on-docker-desktop.md)). No cloud
  environment is planned.

| Earlier proposal | Local replacement |
|---|---|
| AWS: EKS, Aurora, Frankfurt + Ireland | Kubernetes in Docker Desktop; PostgreSQL with CloudNativePG |
| Cloudflare (CDN, firewall, waiting room) | Dropped. A caching layer in front of PublicApi stands in for the CDN; CDN behaviour is covered by tests of the cache headers. The waiting room is our own ([0012](0012-build-our-own-waiting-room.md)) |
| Several availability zones | Three local Kubernetes nodes labelled as zones; stopping a node simulates a zone failure |
| Karpenter (adds servers automatically) | Not shown. A full cluster leaving pods in `Pending` is the lesson instead |
| Disaster recovery to a second region | Backup and point-in-time restore to local S3-compatible storage ([0025](0025-object-storage-and-secrets.md)); optionally a second local cluster as a standby |
| Aurora resizing the writer automatically | Explained, not shown |
| Full-scale load tests | Capacity targets measured at laptop scale ([0015](0015-performance-targets.md)) |

Free components in use: .NET, PostgreSQL + CloudNativePG, Redpanda, Valkey, KEDA,
OpenTelemetry, Prometheus, Grafana, Loki, Tempo, Toxiproxy, k6, Mailpit, SeaweedFS, Stripe test
mode, GitHub Actions within its free minutes.

**Licence check (open):** Docker Desktop is free only for companies with fewer than 250
employees *and* less than $10M annual revenue. If we are over either limit, we use Rancher
Desktop (free, ships k3s) instead; nothing else changes.

## Alternatives considered

| Alternative | Why not |
|---|---|
| A cloud environment run only for M5 load tests | No budget |
| Cloudflare Waiting Room | Needs a Business or Enterprise plan; scheduled on-sales need an Enterprise add-on |

## Consequences

- Cross-region failover (design-decisions §10) can't be demonstrated; the disaster-recovery
  drill is a backup and point-in-time restore.
- Capacity numbers are laptop-sized; correctness targets keep their full values
  ([0015](0015-performance-targets.md)).
- Everything works offline and is reproducible, which suits teaching.
- To stay within free CI minutes, full-cluster system tests run nightly, not on every push.

## Updates

- **2026-10-18:** The Docker Desktop licence check is closed: the free plan applies (details and
  fallback in [0020](0020-local-kubernetes-on-docker-desktop.md)).
