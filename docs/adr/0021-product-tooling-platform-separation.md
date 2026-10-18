# 0021. Product, tooling and platform separated in one repository

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0004](0004-payment-providers-stripe-and-localpay.md) · [0017](0017-scaling-lab-and-load-generator.md) · [0018](0018-capacity-from-configuration-not-code.md) · [0022](0022-disposable-lab-environment.md) · replaces design-decisions §12 (draft layout)

## Context

The project contains the system itself plus tools that drive, fake and watch it: the Scaling
Lab, the load generator, LocalPay, k6 tests, chaos scripts. The product must stay free of test
and monitoring tooling, and must build, test, ship and run without it.

## Decision

**One repository, strictly separated into product, tooling and platform.**

```
EventTicketing/
├─ product/                     the system; deployable on its own
│  ├─ src/                      Shared, Modules, Hosts (incl. ServiceDefaults)
│  ├─ tests/                    unit, integration, architecture
│  ├─ deploy/                   chart (incl. its PostgreSQL cluster, Valkey, Redpanda),
│  │                            values per environment (incl. lab), dashboards, alerts
│  └─ EventTicketing.slnx
├─ tooling/                     everything that drives, fakes or watches the system
│  ├─ lab/                      ScalingLab, LoadGenerator
│  ├─ simulators/LocalPay/
│  ├─ load-tests/               k6 scenarios and thresholds; results/ holds kept baselines
│  ├─ checks/                   invariant checker
│  ├─ chaos/                    Toxiproxy profiles, pod-kill and failover scripts
│  ├─ deploy/                   lab chart, lab dashboards
│  └─ Tooling.slnx
├─ platform/                    local cluster setup: KEDA, CloudNativePG operator, metrics-server,
│                               observability (Prometheus, Grafana, Loki, Tempo), ingress,
│                               SeaweedFS, Mailpit
├─ dev/AppHost/                 Aspire: product hosts + LocalPay (from its Dockerfile) + Mailpit
├─ docs/                        brief, decision log, ADRs, architecture
├─ lab.ps1                      lab up · lab reset · lab down (ADR-0022)
└─ .github/workflows/           product-ci · tooling-ci · system-tests (nightly)
```

| Part | Belongs to | Why |
|---|---|---|
| Modules, hosts, Shared, ServiceDefaults (OpenTelemetry, health checks) | product | |
| Unit, integration and architecture tests | product | they define "done" for product code and run on every product build |
| Operational settings: load-shedding limits, worker parallelism, cache lifetimes | product (configuration) | the lab turns them down; it never adds code ([0018](0018-capacity-from-configuration-not-code.md)) |
| Deployment chart, autoscaling objects, product dashboards and alerts | product (deployment) | operating the product needs them |
| Scaling Lab, LoadGenerator, LocalPay, k6 tests, invariant checker, chaos scripts | tooling | they drive, fake or watch the product |
| Operators and shared services | platform | used by both, owned by neither |
| Aspire AppHost | dev composition | assembles the product and simulators for local development |

**Monitoring splits in two.** The product must be observable on its own: it emits telemetry,
exposes health checks and ships its own dashboards and alerts, because production operation
needs them. The Scaling Lab is just one consumer of that telemetry.

**What may cross the line** (the contract between product and tooling):

1. HTTP APIs, described by OpenAPI documents; tooling uses clients generated from them.
2. Telemetry: metric names and labels, documented in a metrics catalogue. Renaming a metric is
   a contract change.
3. Kubernetes labels on product deployments.
4. Documented configuration settings.
5. One read-only database login for the invariant checker. It must read across module schemas,
   which is exactly why it can't live inside the product. The login exists only in the lab
   environment.

Test data goes in through the APIs, never as direct database inserts (for example, a
50,000-seat venue is created with a seat-map import, which is a real organizer feature).

**Rules, enforced by a CI check:**

- Nothing under `product/` references `tooling/`: no projects, packages, images or charts.
- Tooling never references product assemblies, not even `*.Contracts`.

**Deployment:** Kubernetes namespaces `ticketing` (product), `lab` (tooling), `observability`
and `platform`. The lab environment runs the same product images as any other environment; only
`values-lab.yaml` differs (LocalPay as payment provider, the database address pointing at
Toxiproxy, low limits, the demo scaling profile).

**Pipelines:** `product-ci` (build, tests, images), `tooling-ci` (build tooling, LocalPay image)
and `system-tests` (nightly: product and tooling in kind, k6 and the invariant checker).

## Alternatives considered

| Alternative | Why not |
|---|---|
| Two repositories | Hard boundary and separate release cycles, but every API change becomes two pull requests plus version pinning: too much overhead for a small team |
| Folders inside one solution (the earlier §12 draft: `tools/`, `tests/LoadTests`) | Nothing stops product code from referencing tooling |

## Consequences

- An API change and the matching tooling update go in one pull request, which shows the team
  how the contract works.
- The metrics catalogue and OpenAPI documents must be kept up to date.
- Splitting the repository later is one `git filter-repo`, because only contracts cross the line.
- The project structure inside `product/src/Modules` still depends on the open layout question
  (design-decisions §3).
