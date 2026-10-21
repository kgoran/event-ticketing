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
| http://localhost:18888 | Aspire Dashboard: traces, logs and metrics, each instance as its own replica |

`kubectl --context kind-eventticketing …` reaches the cluster (or `.tools/bin/kubectl`).

## What runs

| Namespace | What |
|---|---|
| `platform` | Traefik (the gateway), Keycloak, Mailpit, the Aspire Dashboard |
| `cnpg-system` | the CloudNativePG operator |
| `keda` | KEDA, which runs PublicApi's autoscaler when the Scaling Lab's **Auto** is on ([ADR-0054](adr/0054-publicapi-autoscaling-switch-in-the-lab.md)) |
| `kube-system` | metrics-server, which measures each pod's CPU for `kubectl top` and the autoscaler |
| `eventticketing` | the database (release `eventticketing-database`) and the product (release `eventticketing`): PublicApi ×3, WebhookIngress ×2, Workers ×2, BackOfficeApi, the fan site |
| `lab` | LocalPay, the load generator, the Scaling Lab (release `eventticketing-tooling`) |

The Migrator runs as a Job before each deploy of the product. A deploy keeps the instance counts
that run (someone may have changed them in the Scaling Lab, or the autoscaler did), and the Auto
switch and its speed as they are; `reset` restores what `product/deploy/chart/values-lab.yaml` says:
Auto off, PublicApi at 3.

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
