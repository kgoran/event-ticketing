# 0017. Scaling Lab and load generator

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0015](0015-performance-targets.md) · [0018](0018-capacity-from-configuration-not-code.md) · [0019](0019-autoscaling-signals.md) · [0021](0021-product-tooling-platform-separation.md) · [0022](0022-disposable-lab-environment.md)

## Context

The team needs to *see* autoscaling happen: a slider for the request rate, live numbers, and
instances of the application and the database appearing and disappearing. The point is to
show how it works, not to reach high numbers.

k6 is good for pass/fail target tests, but its only executor that can be adjusted during a
run changes the number of virtual users, not requests per second. Virtual users wait for each
response before sending the next request, so when the system slows down they send less. That
hides exactly the overload we want to show (coordinated omission).

## Decision

Two small tools in `tooling/lab/`, deployed only in the lab environment:

- **Scaling Lab:** a Blazor Server app with the controls and the live view.
- **LoadGenerator:** a .NET worker that sends requests at the set rate, no matter how slowly
  responses come back (open model). It reports both the rate it was asked for and the rate it
  actually achieved, so it can never become a hidden bottleneck, and it caps its requests in
  flight. One instance is enough at the lab's ≤ 200 requests/s.

```
┌─ Scaling Lab ───────────────────────────────────────────────── LAB CAPACITY LIMITS ON ─┐
│ rate [═══════█══════════] 70 req/s     mix  browse 70% · hold 25% · checkout 5%        │
│ capacity per instance 20 tx/s      scaling (x) demo speed  ( ) realistic               │
│ presets  [step] [spike] [pre-scale] [kill pod] [fail over primary] [slow database]     │
├────────────────────────────────────────────────────────────────────────────────────────┤
│ req/s    offered 70 · served 66 · rejected 4                                           │
│ latency  p50 110 ms · p95 240 ms · p99 310 ms                                          │
│ PublicApi  [####.]  4 ready / 5 wanted     Workers [##] 2     WaitingRoom [#] 1        │
│ database  writer CPU 62% · 2 replicas (CPU 81%) · lag 0.4 s · 140 connections          │
│ timeline 10:42:05 PublicApi 2 → 5 (utilization 125%) · 10:42:31 replica 2 ready        │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

```
Scaling Lab ──rate, mix──► LoadGenerator ──HTTP at set rate──► PublicApi × N
     │                                                         Workers × N
     ├──latency──► Toxiproxy (in front of PostgreSQL)          PostgreSQL 1 + R
     │                                                              │ metrics
     ├──── PromQL every 1 s ──── Prometheus ◄── scrape every 1–2 s ─┘
     │                               │
     │                               ▼
     └── Kubernetes API ◄───────── KEDA scales PublicApi, Workers, read replicas
```

- The lab controls the generator over HTTP and Toxiproxy through Toxiproxy's API.
- It reads Prometheus every second (Prometheus scrapes every 1–2 s in the lab) and watches the
  Kubernetes API for pods, autoscaler decisions and database-cluster changes.
- It finds product deployments by their Kubernetes labels.
- Switching between the "demo speed" and "realistic" scaling profiles means changing the
  autoscaling objects through the Kubernetes API, as an operator would.
- **Project-specific:** the lab speaks ticketing. Presets can create an event and schedule its
  on-sale through BackOfficeApi; the screen shows holds per second, seats sold and orders
  confirmed next to the technical numbers. Every preset ends by running the invariant checker,
  so "oversold seats: 0" appears on the same screen as the scaling result.
- **Single user:** the lab runs only on the presenter's laptop, so it has no login and no
  locking.
- Grafana stays the tool for digging deeper; the lab shows only what a classroom needs.
- "Performance counters" on Linux containers are .NET metrics (`System.Diagnostics.Metrics`)
  exported through OpenTelemetry; `dotnet-counters` gives the familiar live view of one pod.

| Preset | What it teaches |
|---|---|
| 1. Step up (e.g. 28 → 84 requests/s) | Scale-out, the gap while new pods start, the autoscaler's formula |
| 2. On-sale spike (to the top of the slider in 10 s) | Autoscaling is slower than a spike: requests are rejected until capacity arrives. Repeat with pre-scaling on and compare. This is why the waiting room exists |
| 3. Drop back | Deliberately slow scale-in; pods shut down without dropping a request |
| 4. Slow database (Toxiproxy) | Requests hold their slots longer → utilization rises → more pods → more database connections → the database gets slower. Autoscaling makes a downstream problem worse; instance caps and circuit breakers are the fix |
| 5. More browsing | Read replicas scale out; new replicas get no traffic until pooled connections are recycled (Npgsql `Connection Lifetime`) |
| 6. Payment burst | Workers' backlog grows, Workers scale out, then back to zero |
| 7. Kill a pod / fail over the primary / deploy under load | The M2 and M3 drills, watched live |

The lab grows with the milestones ([0016](0016-teaching-milestones.md)): generator, slider and
basic charts in M2; database panel in M3; Workers backlog in M4; capacity settings, autoscaling
and presets 1–5 in M5. k6 remains the tool for the pass/fail target tests
([0015](0015-performance-targets.md)).

## Alternatives considered

| Alternative | Why not |
|---|---|
| Grafana and k6 only | No requests-per-second slider, two separate UIs, no scaling timeline |
| A simulation running entirely in the browser (a queueing model) | Runs anywhere and explains the formula, but nothing real happens, so there are no real failures |
| A generic lab reusable for other projects | Not needed: the lab is for this project only |

## Consequences

- Some tooling code to maintain, outside the product
  ([0021](0021-product-tooling-platform-separation.md)).
- The lab depends on product metric names and deployment labels; they are part of the
  product's documented contract.

## Updates

- **2026-10-20 (D20):** The lab's M2 basics are brought forward and run under Aspire now:
  the load generator, the requests-per-second slider, live tiles and charts of offered versus
  achieved requests, outcomes and latency, and the first presets (steady, step 28 → 84, on-sale
  spike, past capacity). Without the lab's capacity limits (M5), one instance of each host serves
  about 200–250 requests/s of the default mix on the owner's machine, so the slider goes to 400
  for now, and the generator to 1,000. Kubernetes, the pod list, Prometheus, Toxiproxy and autoscaling stay where they were
  (M2–M5). How to use it: [the Scaling Lab runbook](../demos/scaling-lab.md). Implementation
  decisions, all within this ADR:
  - **The mix is browsing and holds.** Browsing is anonymous (event list, event page,
    availability); a hold takes one place, best available, and is let go of at once, so places
    keep cycling and nothing is sold. Checkouts join the mix later: they create orders and
    payments, which need LocalPay's headless mode and the invariant checker at the end of a run.
  - **Holds are placed by twenty lab fans** (`lab01`–`lab20` in the development realm, passwords
    equal to the names), each with at most one hold at a time ([0024](0024-domain-scope-m1-m6.md)).
    Their tokens come from the development client's password grant, as for the seeder.
  - **Outcomes:** 2xx is OK; 409 and 429 are refusals, correct behaviour and not errors (T1);
    another 4xx is a client error, a generator bug or a contract change; 5xx, a timeout (5 s) or
    no connection is a failure. Latency is measured for answered requests only, p99 per kind,
    so holds and browsing show against their own targets (T1, T4).
  - **The generator protects itself:** at most 500 requests in flight; past that, or with no lab
    fan free, a request is counted as skipped, never silently dropped. It sends through one plain
    HttpClient without retries, which would hide failures.
  - **1 in 20 of the generator's requests is traced** (head sampling). The product's hosts follow
    the caller's decision, so under load the dashboard keeps a sample of traces instead of
    drowning; metrics still count every request ([observability](../observability.md)).
  - **A hot-section option** sends every hold to one section, where fans compete for the same
    places.
  - **Presets run in the lab's page:** moving the slider or Stop ends one; closing the page leaves
    the last rate set. The lab is single-user, as decided above.
  - **Both tools start with the AppHost** (the lab at http://localhost:5300, the generator's API
    at http://localhost:5301); the generator idles at 0 requests/s.
