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
  work while it serves its first requests: compiling code, building EF Core's queries. At only
  28 requests/s a fresh instance used several times its CPU request, the autoscaler read that as
  load and went to the maximum of 8, and every instance it added was cold too. Precompiled images
  (ReadyToRun) helped a little, not enough. The fix is to let an instance warm up before it says
  it is ready, because the autoscaler ignores instances that aren't ready (D43, next).

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
