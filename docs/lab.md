# The lab cluster

The whole system in a local Kubernetes cluster, built from the checked-out source by one command
([M2 plan](milestones/m2.md), [ADR-0022](adr/0022-disposable-lab-environment.md),
[ADR-0053](adr/0053-the-lab-cluster-addresses-identity-and-platform.md)). Aspire stays the tool for
day-to-day development; the lab is for seeing the system run as it would in production: several
instances, a gateway, a database operator.

## Commands

You need the .NET 10 SDK and Docker Desktop running. Nothing else is installed: the lab downloads
pinned versions of kind, kubectl and Helm into `.tools/` and checks their checksums.

```
dotnet run lab.cs -- up       create the cluster if there is none, then build and deploy the current source
dotnet run lab.cs -- reset    keep the cluster and platform; a fresh database, redeployed, reseeded
dotnet run lab.cs -- status   what runs, and where to open it
dotnet run lab.cs -- check    does it work: hosts ready, addresses answering, sign-in, a burst of load, T2's invariants
                   --invariants  … only T2's invariants (tooling/checks/invariants.sql)
dotnet run lab.cs -- deploy   roll the product out from the current source, as a release would
                   --load 100  … with 100 requests/s running, and count the failed ones (T10)
                   --restart   … every host again, even if nothing changed
                   --during-on-sale "<why>"   … although an on-sale's rush is on
dotnet run lab.cs -- flag     the feature flags; `flag PauseHolds on` switches one without a deploy
dotnet run lab.cs -- drill failover            the database's primary crashes under load (T10, then T2)
dotnet run lab.cs -- drill zone [zone-b|zone-c] a zone's node stops under load, then starts again
dotnet run lab.cs -- down     delete the cluster and everything in it
```

- **`up` from nothing takes about 5–8 minutes**: the cluster, the platform, nine images, the
  product and the demo data. **Again, with nothing changed, about 30 seconds.** Images are
  rebuilt only when the sources they come from change: their tag is a hash of those sources.
- **Stop the AppHost first.** The lab answers on Aspire's ports, so the two can't run together.
- **Offline:** run `up` once while online. Images, charts and tools are cached after that.

## Where things are

The usual ports, as under Aspire:

| Address | What |
|---|---|
| http://localhost:5100 | fan site (ana, ben, cid, …; password = name) |
| http://localhost:5101 · 5102 · 5103 | PublicApi · BackOfficeApi · WebhookIngress (the `.http` files work unchanged) |
| http://localhost:5201 | LocalPay |
| http://localhost:5300 | Scaling Lab, with the **Instances** panel |
| http://localhost:8025 | Mailpit |
| http://localhost:8180 | Keycloak (`status` prints the admin password) |
| http://localhost:3000 | Grafana: the dashboards, and Explore for metrics, logs and traces (no sign-in needed; `status` prints the admin password) |

`kubectl --context kind-eventticketing …` reaches the cluster (or `.tools/bin/kubectl`).

## What runs

Three nodes, each its own zone (D47): `eventticketing-control-plane` is `zone-a`, the two workers
are `zone-b` and `zone-c`. Stopping one is how a zone fails. `status` shows each node's zone, and
which database instance is the primary. A cluster made before M3 had one node: `down`, then `up`.


| Namespace | What |
|---|---|
| `platform` | Traefik (the gateway), Keycloak, Mailpit, and the observability stack: the OpenTelemetry Collector, Prometheus, Loki, Tempo, Grafana (D55) |
| `cnpg-system` | the CloudNativePG operator |
| `keda` | KEDA, which runs PublicApi's autoscaler when the Scaling Lab's **Auto** is on ([ADR-0054](adr/0054-publicapi-autoscaling-switch-in-the-lab.md)) |
| `kube-system` | metrics-server, which measures each pod's CPU for `kubectl top` and the autoscaler |
| `eventticketing` | the database (release `eventticketing-database`: a primary and two standbys, one per zone) and the product (release `eventticketing`): PublicApi ×3, WebhookIngress ×2, Workers ×2, BackOfficeApi, the fan site, and Valkey (browsing's shared cache, D51) |
| `lab` | LocalPay, the load generator, the Scaling Lab (release `eventticketing-tooling`) |

The Migrator runs as a Job before each deploy of the product. A deploy keeps the instance counts
that run (someone may have changed them in the Scaling Lab, or the autoscaler did), and the Auto
switch and its speed as they are; `reset` restores what `product/deploy/chart/values-lab.yaml` says:
Auto off, PublicApi at 3.

## Seeing it: Grafana (D55)

Every host and every tool sends its telemetry over OTLP to one place, the **OpenTelemetry
Collector**, which passes metrics to **Prometheus**, logs to **Loki** and traces to **Tempo**.
**Grafana** (http://localhost:3000) shows all three. Prometheus also scrapes the database instances
and PgBouncer (CloudNativePG's exporters) and Kubernetes itself (kube-state-metrics). Everything is
kept on the pods for six hours, and goes when a pod restarts: it's a lab.

| Dashboard (folder EventTicketing) | What it answers |
|---|---|
| **Services** | One host at a time (the **Service** picker): requests per second, errors, p99, requests in progress, CPU, memory and database connections per instance, calls out, and its warnings from Loki |
| **Purchases** | Hold and checkout results, how orders end, payments, webhooks, refunds, the outbox's messages and their lag, tickets and emails |
| **Database** | Which instance is primary, replication lag, transactions per instance (the primary's writes, the replicas' browsing), connections, and PgBouncer's clients, servers and waits |

Every panel's **i** says what it shows and how to read it. The dashboards ship with the product's
chart (`product/deploy/chart/dashboards/`, one ConfigMap each, which Grafana's sidecar picks up),
so they change with the code they show. To change one, edit it in Grafana, **Export → JSON**,
and replace the file: Grafana doesn't save over a provisioned dashboard.

**From a trace to its logs and back.** Explore → Tempo finds traces (by service, by duration, by
status); a span's **Logs for this span** opens the lines that service wrote in that trace. From a
log line in Loki, the **trace_id** field opens its trace. The load generator traces 1 in 20
requests (docs/observability.md), so a lab run has traces to look at without keeping every one.

## Drills (T10)

Each drill runs 100 requests/s through the gateway, breaks something, and times the way back from
outside: when the database had a new primary, which seconds had failed requests, then T2's
invariants (D59).

- **`drill failover`**: the primary crashes (its pod is deleted at once, with no time to finish
  anything). CloudNativePG promotes a standby, and the `-rw` service follows it.
- **`drill zone`**: a worker node stops, as a zone would fail, and starts again afterwards. Without
  a zone named, the one with the database's primary, if it is a worker's.
  zone-a is never stopped: it is the lab's way in, with the port mappings, the gateway, Keycloak,
  Mailpit, the operators and the tooling, which stand for the outside world.

Measured on 2026-10-21:

| Drill | New primary after | Requests failed | Within |
|---|---|---|---|
| Failover (crash) | 22 s | 1,692 of 8,586 | 24 s: **T10's 60 s held**; T2 held |
| Zone (the primary's zone) | 59 s | 6,025 of 13,203 | 63 s; T2 held |
| Zone (another zone) | 53 s | 4,006 of 10,903 | 59 s; T2 held |

What the drills taught:

- **A graceful delete is not a crash.** The first drill deleted the primary's pod normally:
  PostgreSQL's smart shutdown then waits up to 180 s for clients to leave, and the pools keep their
  connections, so the failover took 129 s. The drill now crashes it; and a planned stop (a drain, an
  upgrade) gives clients 10 s (`smartShutdownTimeout`).
- **A zone without the primary still fails requests for most of a minute**: until Kubernetes marks
  the node NotReady, Traefik keeps sending requests to the instances there. CI's run of that case:
  1,737 of 31,216 failed, within 55 s.
- **A zone failure is noticed late.** Kubernetes marks a silent node NotReady after about 40 s, and
  only then does anything move: most of a zone drill's minute is waiting to know. The hosts on the
  lost node are replaced after 30 s more (their tolerations; Kubernetes' default is 5 minutes).
- **Everything fails while the primary is gone**, browsing too, because every read goes to it.
  Browsing from the replica (RR1) and the cache (VK1) are what change that.
- **A pod on a lost node still looks ready** to the API for a while. The lab asks CloudNativePG
  which instances are healthy.

## Connections: PgBouncer, and the replicas (D49, D50)

The hosts reach the primary through PgBouncer (CloudNativePG's `Pooler`, two instances, transaction
mode): each host keeps its pool of 20, PgBouncer keeps at most 2 × 40 connections to PostgreSQL. With
PublicApi at 8 instances under 300 requests/s the primary had **33 connections**. The Migrator
connects to the primary directly. Browsing (Catalog's event list, pages and seat maps, when the
cache misses) reads a replica through its own connection string; its connections are recycled
after a minute, so they spread over the replicas and follow them.

PgBouncer needed tuning for a failover. Its defaults wait 15 s before trying a server again and
let clients queue for 2 minutes: the failover drill had 42 s of errors and a third of its usual
throughput. With `server_login_retry` 1 s, `server_connect_timeout` 3 s and `query_wait_timeout`
10 s, it is back to 26 s.

| Failover drill | Requests failed | Within |
|---|---|---|
| M2 (no cache, the primary for everything) | 1,692 | 24 s |
| Browsing's cache and snapshots | 1,179 | 25 s |
| And PgBouncer, defaults | 2,540 | 42 s |
| And PgBouncer, tuned | 1,555 | 26 s |

The drill also found a bug in the load generator: answers broken half-way by the failover threw
an exception it didn't expect, those requests were never counted as finished, and after 500 of
them it skipped everything. It now counts every request, in a `finally`.

## Browsing's cache and availability snapshots (D52, D53)

PublicApi keeps the event list, event pages and seat maps in Valkey, for everyone alike, signed in
or not: 30 s, or until an on-sale opens or an event starts if that comes sooner, and a day for a
seat map's version. BackOfficeApi evicts an event's answers (and the list) when an organizer
changes it. Availability comes from a snapshot per on-sale event, rebuilt about once a second by
one Workers instance; a sold-out section is refused from a flag, without the database. If Valkey
is down, all of it goes to the database again: slower, still answered.

At 200 browsing requests/s, measured on 2026-10-21:

| | The primary's transactions/s | Its CPU |
|---|---|---|
| No cache | 780 | 365m |
| Output cache | 480 | 297m |
| And availability snapshots | **89** (idle: 71) | **89m** |

In the failover drill, cached browsing keeps answering while the primary is gone: 1,179 requests
failed instead of 1,692. Holds and availability (once its 5-second snapshot is gone) still fail.

## T2's invariants

`tooling/checks/invariants.sql` holds T2's rules across the modules' schemas: no place sold twice,
no ticket without a confirmed seat, every paid order confirmed or refunded, no refund paid twice,
every confirmed order with its tickets (D58). `check` runs them last, and `check --invariants`
alone; both print each violation, with the place or order it concerns. They run in one read-only
transaction on a replica, so every schema is read at the same moment and nothing can be written.
Work that finishes asynchronously gets five minutes before it counts. The product never reads
across schemas (ADR-0002); only the lab's checker does. `InvariantCheckerTests` plants a
violation in the lab's data and expects the checker to name it.

## Deploys under load (T10)

`lab deploy` is a release: it builds the images of the current source, runs the Migrator, and
rolls out each host whose image or settings changed. The database and the tooling stay as they
are, so the load generator keeps running and measuring. With `--load 100` it sends 100
requests/s through the gateway from 10 s before the rollout until 10 s after, and counts the
requests that failed against T10: under 0.1% ([ADR-0015](adr/0015-performance-targets.md)).
`--restart` rolls every host out again with the same images, to measure without a change.

What makes a rollout fail nothing, each part in the chart:

- **One new instance at a time, never one fewer** (`maxSurge: 1`, `maxUnavailable: 0`): an old
  instance stops only once its replacement passes readiness.
- **Readiness means warm** (D43): a new instance takes traffic only after its warm-up.
- **A stopping instance keeps answering for 5 s** (`preStop`), while Traefik removes it, then
  finishes its requests in flight after SIGTERM, within the 30-second grace period.

Measured on 2026-10-21, every host rolled out again (`deploy --restart --load 100`): **0 of 8,030
requests failed** with PublicApi at 3 instances, and **0 of 7,096** with the autoscaler on.

### No release during an on-sale (DP4)

Before anything else, `lab deploy` reads the on-sale schedule from PublicApi, as anyone may, and
refuses (exit code 2) while an on-sale's rush is on: from 30 minutes before it opens until an hour
after (D44). It names the events and says until when:

```
== Deploy gate: no release during an on-sale (DP4)
  Refused: Demo Concert (on sale 19:33 UTC), Failure Scenarios (on sale 19:33 UTC): no release until 20:33 UTC.
  Wait until then, or, if it can't wait: deploy --during-on-sale "<why>".
```

`--during-on-sale "<why>"` deploys anyway and prints the reason, for the fix that can't wait.
`up` and `reset` aren't gated: they build the environment rather than release into it. Seeding
puts the demo on sale at once, so for an hour after `up` or `reset` a plain `deploy` is refused,
which is also how the demo and the lab workflow show the gate.

Since M3 the fan site keeps fans' tokens and its cookie's keys in Valkey and runs twice (D54, D67):
a rollout no longer signs fans out. A fan whose circuit was on a stopped instance sees the page
reload, still signed in. A chart's change to an instance count reaches a running lab with `reset`
(a deploy keeps the counts that run).

## Autoscaling

The Scaling Lab's **Auto** checkbox hands PublicApi's count to KEDA's autoscaler: between 1 and 8
instances, aiming for 70% of the CPU each instance requests (150m, which one instance reaches at
about 20 requests/s of the default mix). Off is KEDA's pause, and − / + set the count again. The
switch is the `autoscaling.keda.sh/paused` annotation on the `ScaledObject`, so the same thing
works from a terminal:

```
kubectl --context kind-eventticketing -n eventticketing annotate scaledobject publicapi autoscaling.keda.sh/paused-     # Auto on
kubectl --context kind-eventticketing -n eventticketing annotate scaledobject publicapi autoscaling.keda.sh/paused=true # Auto off
kubectl --context kind-eventticketing -n eventticketing get hpa    # what the autoscaler measures and wants, while on
```

CPU is the signal until Prometheus arrives in M3 ([ADR-0019](adr/0019-autoscaling-signals.md)
update); then it becomes requests in progress per instance.

What the first runs showed (2026-10-21, default mix, demo speed):

- **Warm instances scale as calculated.** At 84 requests/s the count settled at 5 (84 ÷ 20 =
  4.2) at 56–70% CPU, and 30 s after the load stopped it was back to 1. Not one of 16,784
  requests failed, and p99 stayed under 100 ms.
- **Cold instances fool a CPU autoscaler.** An instance that has just started does its first-time
  work while it serves its first requests. At only 28 requests/s a fresh instance used several
  times its CPU request, the autoscaler read that as load and went to the maximum of 8, and every
  instance it added was cold too. Three things fixed it (D43), each measured:
  1. **Warm up before ready.** PublicApi sends itself its browsing requests a few dozen times
     (about 5 s) and only then passes its readiness check; the autoscaler ignores instances that
     aren't ready, and Traefik sends them nothing. A new instance is ready 10 s after it starts.
  2. **No dynamic PGO** (`DOTNET_TieredPGO=0`). .NET recompiles busy methods once more in the
     background, which cost a new instance 621m of CPU in its first half-minute of holds instead
     of the 140m it settles at. Without it: 224m, and the same steady CPU.
  3. **A 20-second scale-up window.** The autoscaler scales up only to the lowest count it wanted
     over 20 s, so one instance's first moments don't buy another.

  Then the step behaves: 1 → 2 at 28/s, 2 → 5 (and one more, briefly) at 84/s, back down after
  Stop, with no failed request. The count needs about 60–70 s to catch up with the step: most of
  that is measuring (metrics-server and the autoscaler each look every 15 s), the rest the
  20-second window. Precompiled images (ReadyToRun) were tried too: they cut the first spike only
  from 569% to 420%, cost 50 MB per image and three more minutes per build, and aren't used.

## Three things the lab taught on its first day

Each was fixed in configuration, not code ([ADR-0018](adr/0018-capacity-from-configuration-not-code.md)):

1. **Readiness checks need time under load.** `/health` checks the database. With Kubernetes'
   default 1-second timeout, busy but healthy instances were taken out of rotation, which loaded
   the rest more. The probes now allow 5 seconds.
2. **A stopping instance must keep answering briefly.** The gateway learns about a stopped
   instance a moment later, and requests sent in that moment failed. Instances now wait 5 seconds
   (`preStop`) before shutting down.
3. **More instances need more database connections.** Every instance keeps its own connection
   pool. Five PublicApi instances under load held 96 of PostgreSQL's 100 connections, and a new
   instance couldn't connect at all. Pools are now capped at 20, and the database allows 300,
   enough for the most instances the lab allows ([ADR-0019](adr/0019-autoscaling-signals.md) update).

With those fixes, PublicApi went from 2 to 5 instances and back at 100 requests/s without a single
failed request.

## And on its second day

4. **A CPU autoscaler takes a new instance's first minute for load** ([Autoscaling](#autoscaling)):
   warm up before ready, no dynamic PGO, a 20-second scale-up window (D43).
5. **A column renamed in one release breaks the release still running**: 26% of requests failed
   during the rollout. Expand, then contract ([database changes](database-changes.md)).
6. **Helm 4 applies server-side, and whoever changed a field last owns it.** After `lab flag`
   changed the flags' file, the next deploy refused: the chart rendered the same flags spelled
   differently, a conflict with `kubectl`'s change. Charts render operator-owned fields exactly as
   they run (`lookup`), and the lab's upgrades use `--force-conflicts` for a flag a release adds.

## And when it got dashboards (M3)

7. **The first dashboards found two bugs that no test had.** Every command's p99 read exactly 5 s:
   the histograms measured seconds in OpenTelemetry's default buckets, which are for milliseconds,
   so everything fell into the first one (now `HistogramBuckets`, checked by an architecture test).
   And 95% of all traces were the outbox's polling, one per second per loop, burying the requests'
   (background calls are no longer traced on their own). Telemetry is code: look at it under load.
8. **An observability stack has its own capacity plan.** Grafana 13 was killed at 384 MB on the
   first dashboard with live data; Prometheus held 98,000 series after half an hour, 60,000 of them
   the API server's own metrics nobody read (15,000 now). Limits from measurements, and scrape
   only what a dashboard or an alert reads. All the lab's pods together: about 6.3 GB.
