# 0029. One container image per host, built by the .NET SDK

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0008](0008-hosts.md) · [0016](0016-teaching-milestones.md) · [0021](0021-product-tooling-platform-separation.md) · [0022](0022-disposable-lab-environment.md) · design-decisions §13 (Q15)

## Context

There are seven product hosts ([0008](0008-hosts.md)). The disposable lab builds every image
from source at each start ([0022](0022-disposable-lab-environment.md)), and M2 introduces the
deployment pipeline.

## Decision

- **One image per host:** PublicApi, WaitingRoom, WebhookIngress, BackOfficeApi, CheckInApi,
  Workers, Migrator.
- **Built with the .NET SDK's built-in container publishing** (`dotnet publish` with the
  `PublishContainer` target). There are no Dockerfiles for product hosts.
- **Minimal base images:** Microsoft's chiseled .NET images, which run as non-root by default.
- **All images in a release come from the same commit** and are tagged with its SHA; a release
  is deployed as a set.
- **In the lab, images reach the cluster without an external registry.** Either the cluster
  sees locally built images directly, or `lab up` starts a local registry container. Which one
  works with Docker Desktop's kind mode is verified in M2.

## Alternatives considered

| Alternative | Why not |
|---|---|
| One image that selects the host at startup | One build, but every deployment ships every host's configuration, the composition root becomes a runtime switch, and it's harder to see what runs where |
| Hand-written Dockerfiles | More control and more to maintain; nothing we need that SDK publishing lacks |

## Consequences

- Seven product images per build. They share base layers, so builds stay fast enough for the
  lab's startup limits.
- Each Kubernetes Deployment names its host's image explicitly.
- Tooling (Scaling Lab, LoadGenerator, LocalPay) uses the same SDK publishing.

## Updates

- **2026-10-21 (D25):** Verified: images reach the kind cluster with **`kind load`**, without a
  registry (PublicApi built in 38 s, loaded in 9 s, running as non-root). The base stays the plain
  **`noble-chiseled`** image: the product needs neither ICU (invariant globalization) nor the
  system's time zones (NodaTime carries its own). In the lab, images are tagged with a hash of the sources they are
  built from, committed or not: the same sources get the same tag, and a change to a chart or a
  document rebuilds nothing. A release (DP2) is tagged with its commit, as decided above. [M2 plan](../milestones/m2.md).
