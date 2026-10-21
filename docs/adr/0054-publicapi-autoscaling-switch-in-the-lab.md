# 0054. PublicApi's autoscaling switch in the lab, from M2

- **Status:** Agreed
- **Date:** 2026-10-21
- **Related:** [0016](0016-teaching-milestones.md) · [0017](0017-scaling-lab-and-load-generator.md) · [0018](0018-capacity-from-configuration-not-code.md) · [0019](0019-autoscaling-signals.md) · [M2 plan](../milestones/m2.md) (D37–D41)

## Context

Since SL1 the Scaling Lab changes PublicApi's instance count by hand, the hand-operated version
of M5's autoscaler (D31). On 2026-10-21 the owner asked for an **Auto** checkbox: let Kubernetes
choose the count. [ADR-0019](0019-autoscaling-signals.md) has the agreed design: KEDA, reading
Prometheus, on PublicApi's utilization (requests in progress per instance, target 70%), with a
maximum count and two speed profiles. Of that, the lab has none of KEDA, Prometheus (M3) or
PublicApi's capacity per instance (M5's capacity settings). It does have metrics-server (D27),
which reports CPU.

## Decision

**D37. PublicApi only, brought forward from M5.** The Instances panel gets an **Auto** checkbox for
PublicApi. Every other host keeps its fixed count (D30). M5 keeps the rest of autoscaling:
utilization as the signal, Workers on their backlog, the cron scaler before an on-sale, capacity
settings and the scaling timeline.

**D38. KEDA, as ADR-0019 says.** The lab platform installs KEDA from its pinned chart. The product
chart has a `ScaledObject` for PublicApi, switched on in the lab's values. A deploy must not move
the count the autoscaler (or the operator) chose, so the chart renders each Deployment's
`replicas` as the live value when the Deployment exists, and from the values only on the first
install. The same goes for the switch below.

**D39. Off is KEDA's pause.** Auto off means the `ScaledObject` carries
`autoscaling.keda.sh/paused: "true"`: KEDA removes its autoscaler, and − / + set the count as
before. Auto on removes the annotation: KEDA's autoscaler owns the count between the minimum and
the maximum, and the stepper shows it without changing it. The state lives in the cluster, so
every open lab page shows the same. After `lab up` it is off, at the count in the values (D30).
The lab's role grows by exactly this: read and patch `ScaledObjects`, and read the autoscaler it
makes, in the product namespace.

**D40. CPU for now.** Without Prometheus, the trigger is KEDA's `cpu` trigger: average CPU per
instance as a share of the CPU it requests. PublicApi's CPU request is set so that the target
share is reached at about the lab's 20 requests/s per instance of the default mix (T11's
assumption), measured in the lab. When Prometheus arrives in M3 the trigger becomes ADR-0019's
utilization. CPU's weakness, that it barely moves while requests wait for the database, is shown
in the lab rather than hidden: a hold-heavy mix scales later than browsing.

**D41. One to eight instances, two speeds.** The minimum is 1 and the maximum 8, the most the
database's connection budget allows (ADR-0019 update). The lab switches between ADR-0019's two
profiles:

| Profile | Scale-down waits | Notes |
|---|---|---|
| Demo speed (after `lab up`) | 30 s | |
| Realistic | 5 min (the Kubernetes default) | |

KEDA's polling interval doesn't apply to CPU: Kubernetes' autoscaler reads metrics-server every
15 s, and metrics-server samples every 15 s, so the count reacts 15–45 s after the load changes.
That delay is one of the lab's lessons (ADR-0019: "part of scaling is slow is measuring is slow").

## Alternatives considered

| Alternative | Why not |
|---|---|
| ADR-0019's signal now: Prometheus brought forward from M3, plus KEDA | About a week more, more memory, and a product change for the metrics endpoint, for what M3 brings anyway; the switch, limits and profiles built now don't change when the signal does |
| Kubernetes' own autoscaler (HPA) on CPU, without KEDA | Smaller, but thrown away in M5 when KEDA takes over |
| Auto off by deleting the `ScaledObject` | The chart would put it back at the next deploy, and the lab would need to create objects, not only change them |
| `paused-replicas: N` for the manual count | The stepper would edit the `ScaledObject` instead of the deployment's scale, which is what an operator does without KEDA; `paused` keeps D31's mechanism |
| Wait for M5 | The owner asked for it now; it also lets T11's step be tried long before M5 |

## Consequences

- KEDA runs in the lab from M2 (a few hundred MB), and the product chart needs its CRDs where
  autoscaling is on.
- On one laptop the extra instances share the same CPU and database: the switch shows how
  autoscaling behaves, not more capacity ([M2 risks](../milestones/m2.md#risks)).
- T11 (a 28 → 84 step served by about three times the instances within 60 s) can be tried in the
  lab now, on CPU; it is verified on utilization in M5.
- Changing the signal in M3 is a change to the `ScaledObject`'s trigger and nothing else.
