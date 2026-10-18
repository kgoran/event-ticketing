# 0037. Kiota for the tooling's API clients

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0017](0017-scaling-lab-and-load-generator.md) · [0021](0021-product-tooling-platform-separation.md) · [0030](0030-runtime-and-api-style.md) · [M1a plan](../milestones/m1a.md)

## Context

Tooling uses the product only through its HTTP APIs, described by OpenAPI documents that are
generated at build time and committed ([0021](0021-product-tooling-platform-separation.md),
[0030](0030-runtime-and-api-style.md)). Hand-written HTTP code in the tooling would drift from
the contract without anyone noticing.

## Decision

- **Kiota** (Microsoft, MIT licence) generates C# clients from the committed OpenAPI documents.
- The generated clients live in `tooling/` and are committed. A script regenerates them;
  `tooling-ci` fails if they are out of date.
- The Kiota CLI is pinned as a local .NET tool (tool manifest), so everyone generates the same
  code.
- First used by the demo seeder in M1a; later by the LoadGenerator and the Scaling Lab presets.

## Alternatives considered

| Alternative | Why not |
|---|---|
| NSwag | Mature, with simpler output, but tied to its own toolchain; Kiota is Microsoft's current direction for OpenAPI clients |
| Refitter (Refit interfaces from OpenAPI) | Adds the Refit runtime and another layer of conventions |
| Hand-written `HttpClient` code | Drifts silently from the contract |

## Consequences

- A contract change in the product, regenerated, shows up as compile errors in the tooling in
  the same pull request ([0021](0021-product-tooling-platform-separation.md)).
- Generated code is verbose; it is never edited by hand.
- Kiota's runtime packages (MIT) are used only in tooling.
