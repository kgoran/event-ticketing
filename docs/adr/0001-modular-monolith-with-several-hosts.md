# 0001. Modular monolith, deployed as several stateless hosts

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0002](0002-module-boundary-rules.md) · [0008](0008-hosts.md) · design-decisions §1

## Context

The project must teach horizontal scaling, high availability and module boundaries.
Microservices from day one would bring distributed transactions and operational cost without
teaching anything a well-bounded monolith can't. A monolith without boundaries teaches nothing
about boundaries.

## Decision

- One codebase with strict module boundaries ([0002](0002-module-boundary-rules.md)).
- The same code runs as several process types (hosts), each deployed and scaled on its own
  ([0008](0008-hosts.md)). Each host chooses which modules' endpoints and consumers it runs.
- Module contracts are designed as if calls were remote: asynchronous, plain data, no lazy
  loading across a boundary.
- Extraction path: to turn a module into a service, replace its in-process module API with a
  gRPC client and its in-process event delivery with Kafka. Nothing else should need to change.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Microservices from day one | Distributed transactions and operational cost from the start; nothing extra to learn that the modular monolith can't show |
| Layered monolith without module boundaries | Nothing to learn about boundaries; no realistic extraction path |

## Consequences

- Scaling is per host, not per module. A module can still get its own deployment by switching
  on only its endpoints or consumers in a host's configuration (for example `workers-ticketing`).
- Boundaries are only as strong as their enforcement: architecture tests in CI and one database
  login per module ([0002](0002-module-boundary-rules.md)).
