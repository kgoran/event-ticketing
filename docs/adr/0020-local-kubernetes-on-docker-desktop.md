# 0020. Kubernetes in Docker Desktop for the lab, Aspire for development, kind in CI

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0003](0003-core-platform-technologies.md) · [0005](0005-no-paid-services-local-first.md) · [0022](0022-disposable-lab-environment.md) · design-decisions §11

## Context

Everything runs locally ([0005](0005-no-paid-services-local-first.md)), and the team already
uses Docker Desktop. There are two ways to get several instances of a service on Docker Desktop,
and only one of them autoscales:

- **Docker Compose (`--scale`) or Aspire (`WithReplicas`)** give a fixed number of instances
  that you change by hand. Nothing scales automatically.
- **Kubernetes** adds and removes instances based on load (Kubernetes autoscaler, KEDA).

All local Kubernetes options (Docker Desktop's built-in Kubernetes, kind, k3s, k3d, minikube)
expose the same Kubernetes API, so deployment charts and autoscaling behave the same on each.

## Decision

Three ways to run, all on Docker Desktop:

```
day-to-day development   Aspire (press F5)        fast, no Kubernetes, fixed instance count
lab environment          Kubernetes in Docker     same deployment charts as the other environments;
                         Desktop, kind mode,      autoscaling, failover drills, deploys under load,
                         3 nodes                  the Scaling Lab
CI                       kind in GitHub Actions   same engine; full cluster tests nightly
```

- **Docker Desktop's built-in Kubernetes, kind mode.** This needs Docker Desktop 4.38 or later,
  a signed-in Docker account, and the containerd image store.
- **Three nodes**, labelled as zones: lets us spread pods across zones and run a realistic
  failover drill by stopping one node. Autoscaling works on a single node too, so start with one
  if memory is tight and add the others in M3.
- **Docker Desktop's cluster comes bare.** One shared setup script installs metrics-server,
  KEDA, the CloudNativePG operator, Prometheus/Grafana and the other platform components
  ([0021](0021-product-tooling-platform-separation.md)).
- **Who runs what:** demos run on the project owner's laptop. Developers use Aspire day to
  day and don't need Kubernetes. Anyone working on deployment or high-availability tasks can run
  the lab locally if their machine allows; otherwise the nightly CI run covers it.
- **Sizing:** the lab is sized for 16 GB of RAM (32 GB is comfortable). If the presenter's
  laptop has less, run one node and fewer replicas.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Docker Compose or Aspire replicas only | No autoscaling at all |
| Our own small autoscaler that starts and stops Compose containers | We'd rewrite the Kubernetes autoscaler and a load balancer that discovers containers; none of it carries over to real projects |
| k3s / k3d | A fine lightweight Kubernetes, but adds nothing Docker Desktop doesn't already give us |
| Rancher Desktop (free, ships k3s) | The fallback if the Docker Desktop licence is a problem (open check, [0005](0005-no-paid-services-local-first.md)) |
| minikube | No advantage over the options above |

## Consequences

- The lab and CI use the same deployment charts as any future environment would.
- Kubernetes knowledge is needed by whoever runs the lab, not by every developer.

## Updates

- **2026-10-18:** Licence check done. The company has fewer than 250 employees and its annual
  revenue is assumed to be under $10M, so the free Docker Personal plan covers the project owner and the
  developers. Docker's terms don't depend on the app being demo-only or non-production, so if
  the company ever crosses either limit, switch to the free fallback: **Podman Desktop with
  kind** (same engine as CI; Aspire and Testcontainers support it). This replaces Rancher
  Desktop as the preferred fallback, because kind gives several nodes without extra tooling.
- **2026-10-21 (D22, D23):** The lab cluster is made by the **`kind` tool**, pinned and downloaded
  by the lab script, instead of Docker Desktop's built-in Kubernetes. The lab is created and
  deleted by a script ([0022](0022-disposable-lab-environment.md)); Docker Desktop's cluster is
  switched on by hand in its settings and needs a Docker sign-in. It is the same engine as CI and
  as the Podman fallback above. M2 runs **one node**; M3 adds the three zone-labelled nodes for the
  failover drill. [M2 plan](../milestones/m2.md).
