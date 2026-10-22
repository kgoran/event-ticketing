# 0053. The lab cluster: addresses, identity and platform in M2

- **Status:** Agreed
- **Date:** 2026-10-21
- **Related:** [0017](0017-scaling-lab-and-load-generator.md) · [0020](0020-local-kubernetes-on-docker-desktop.md) · [0021](0021-product-tooling-platform-separation.md) · [0022](0022-disposable-lab-environment.md) · [0027](0027-identity-provider-keycloak.md) · [0028](0028-gateway-and-browse-caching.md) · [M2 plan](../milestones/m2.md) (D27–D29)

## Context

M2 moves the system from Aspire into a local Kubernetes cluster. Three things change when it
does:

- **Addresses.** Under Aspire every host has a fixed localhost port, and the `.http` files, the
  runbooks, the realm's redirect URIs and the emails' links all use them.
- **Identity.** Inside the cluster `localhost` means the pod itself, so the services can't reach
  Keycloak at `http://localhost:8180`. A browser can't reach Keycloak's internal name. A token is
  only valid where its issuer matches.
- **Platform.** The cluster comes bare: nothing routes traffic, runs PostgreSQL or collects
  telemetry until we install it.

## Decision

**Addresses: the same ports as under Aspire.** The cluster maps each usual localhost port to a
Traefik listener, and a Gateway API route sends it to the right service:

| Port | Service | Port | Service |
|---|---|---|---|
| 5100 | fan site | 5201 | LocalPay |
| 5101 | PublicApi | 5300 | Scaling Lab |
| 5102 | BackOfficeApi | 5301 | load generator |
| 5103 | WebhookIngress | 8025 | Mailpit |
| 8180 | Keycloak | 18888 | Aspire Dashboard |

Inside the cluster, clients that stand in for the outside world go through Traefik too: the load
generator calls PublicApi through the gateway, so requests are balanced one by one across
instances, as fans' requests would be. Services that talk to each other use the internal names.

**Identity: one issuer, `http://localhost:8180`.** Keycloak's public address is fixed
(`KC_HOSTNAME`), so every token names it as its issuer, whoever asked for it. Its back-channel
endpoints follow the address they were called on (`KC_HOSTNAME_BACKCHANNEL_DYNAMIC`). The
services use Keycloak's internal address as their authority. The discovery document they read
there names the public issuer and internal key and token endpoints. A token from the browser,
the seeder or the load generator validates in every service, and the product doesn't change.

**Platform in M2**, installed by `lab up` from pinned charts and versions:

| Component | Why now |
|---|---|
| Gateway API CRDs and Traefik ([0028](0028-gateway-and-browse-caching.md)) | Routing, and balancing across instances |
| CloudNativePG operator, a one-instance cluster | The database; standby and replicas come in M3 |
| Keycloak in dev mode, the same realm file as Aspire | Sign-in; nothing to keep, the lab is disposable |
| Mailpit | The emails |
| metrics-server | `kubectl top`; KEDA needs it in M5 |
| Aspire Dashboard (standalone) | Traces, logs and metrics over OTLP; shows each instance as its own replica |

The full observability stack (Prometheus, Grafana, Loki, Tempo) comes in M3 with the service
dashboards. KEDA comes in M5, Valkey and SeaweedFS in M3.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Host names such as `fansite.localhost` | Browsers resolve them; .NET clients and curl on Windows don't |
| One port with path prefixes | Breaks sign-in redirects, absolute links and the APIs' root paths |
| An issuer per network, with the product accepting both | A product change for a lab problem, and two kinds of token in circulation |
| Prometheus, Grafana, Loki and Tempo now | 2–3 GB more and a longer start, for dashboards that M3 builds |

## Consequences

- Aspire and the lab can't run at the same time: they use the same ports.
- `.http` files, runbooks, the realm and the seeder work against either.
- When M3 adds the observability stack, the Aspire Dashboard can stay as the quick view or go.
  **Update (M3, D55, D68):** it went. Grafana answers at http://localhost:3000 instead of 18888,
  fed by the OpenTelemetry Collector ([docs/lab.md](../lab.md#seeing-it-grafana-d55)).
