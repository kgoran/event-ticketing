# 0028. Gateway API with Traefik; browse caching in PublicApi

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0003](0003-core-platform-technologies.md) · [0005](0005-no-paid-services-local-first.md) · [0008](0008-hosts.md) · [0020](0020-local-kubernetes-on-docker-desktop.md) · design-decisions §13 (Q14)

## Context

The lab needs a front door for the five public hosts (routing, TLS, coarse rate limits) and
a stand-in for the CDN that [0005](0005-no-paid-services-local-first.md) dropped.

The Kubernetes project announced on 2025-11-11 that ingress-nginx would be retired; maintenance
ended in March 2026, with no further releases or security fixes. The Ingress API itself is
feature-frozen, and Kubernetes recommends Gateway API as its successor (checked 2026-10-18).

## Decision

**Gateway**

- All routing is described with **Kubernetes Gateway API** resources (`Gateway`, `HTTPRoute`).
  That is the part that carries over to other projects.
- The lab's implementation is **Traefik** (MIT licence, light enough for a laptop, implements
  Gateway API), installed as a platform component.
- Coarse per-IP rate limits are a Traefik middleware (implementation-specific). Per-fan limits
  stay in PublicApi.

**Browse caching**

- Browse responses (event list, event pages) are cached with **ASP.NET Core output caching in
  PublicApi, stored in Valkey**. All instances share one cache, and output caching protects
  against cache stampedes (many requests rebuilding the same entry at once).
- Responses also carry correct `Cache-Control` headers, so a CDN could be put in front later;
  automated tests check those headers.
- Seat-map geometry is served under versioned URLs and cached for good; cache lifetimes stay
  short instead of relying on purges.
- This refines [0005](0005-no-paid-services-local-first.md)'s "caching layer in front of
  PublicApi": the cache lives inside PublicApi, shared through Valkey.

## Alternatives considered

| Alternative | Why not |
|---|---|
| ingress-nginx | Retired; no security fixes since March 2026 |
| Envoy Gateway | A strong Gateway API implementation, but a controller plus Envoy proxies is heavier on a laptop |
| NGINX Gateway Fabric, HAProxy | Fine choices; no advantage over Traefik for the lab |
| Varnish in front of PublicApi as a CDN stand-in | Closest to real CDN behaviour, but one more component and its own configuration language |

## Consequences

- Swapping the gateway later changes only the `GatewayClass` and the Traefik-specific
  middlewares; routes stay as they are.
- Caching is product code (output-cache policies per endpoint), which makes caching decisions
  visible in code review.
- PublicApi pods still receive browse traffic (a CDN would absorb it); the cache saves the
  database, not the pods.
