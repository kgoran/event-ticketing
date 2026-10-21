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
| `eventticketing` | the database (release `eventticketing-database`) and the product (release `eventticketing`): PublicApi ×3, WebhookIngress ×2, Workers ×2, BackOfficeApi, the fan site |
| `lab` | LocalPay, the load generator, the Scaling Lab (release `eventticketing-tooling`) |

The Migrator runs as a Job before each deploy of the product. A deploy keeps the instance counts
that run (someone may have changed them in the Scaling Lab); `reset` restores the counts in
`product/deploy/chart/values-lab.yaml`.

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
