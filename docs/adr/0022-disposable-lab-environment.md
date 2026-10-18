# 0022. Disposable lab environment

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0015](0015-performance-targets.md) · [0020](0020-local-kubernetes-on-docker-desktop.md) · [0021](0021-product-tooling-platform-separation.md) · [0025](0025-object-storage-and-secrets.md)

## Context

The lab runs on one laptop and is used for demos and drills. Keeping it alive between
sessions invites drift, leftover state and "works on my cluster" problems.

## Decision

The whole lab environment is disposable: it is rebuilt from the repository whenever it starts.

```
lab up      create cluster → install platform → build images from the current source
            → deploy product + tooling → seed demo data through the APIs → open the Scaling Lab
lab reset   keep cluster and platform; redeploy product, wipe and reseed data   (fast path)
lab down    delete the cluster
```

- **Nothing persists between sessions.** The Scaling Lab has no database; a session's history
  lives in memory and Prometheus and disappears with the cluster.
- **Images are built from the current source at every start**, so the lab always runs the
  code that is checked out. No image registry is needed.
- **Demo data is reseeded through the APIs every time.** The default data set is small, so
  startup stays fast; the 50,000-capacity data set is an option for load tests
  ([0015](0015-performance-targets.md)).
- **Secrets and signing keys are generated per session**, except the Stripe test keys, which
  come from an untracked local file ([0025](0025-object-storage-and-secrets.md)).
- **Results are the only thing kept:** k6 summaries, saved as files (baselines committed under
  `tooling/load-tests/results/`) or as CI artifacts.
- **Startup limits:** `lab up` from cold in under 15 minutes; `lab reset` in under 3 minutes.
- **Scripts in PowerShell 7**, which runs the same on Windows laptops and Linux CI runners.

## Alternatives considered

| Alternative | Why not |
|---|---|
| A long-lived lab cluster | Drift, leftover data, and nobody remembers how it was built |
| A .NET 10 single-file C# app (`dotnet run lab.cs`) for the scripts | Keeps the team in C#; a reasonable swap if the team prefers it, since the commands stay the same |

## Consequences

- Drills that need state (a failover, a point-in-time restore) happen within one session.
- There is nothing to back up or migrate in the lab itself.
- Startup time must be watched; cached base images keep it within the limits.
- It teaches reproducible environments built from code: servers are replaced, not repaired.
