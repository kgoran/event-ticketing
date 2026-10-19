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

## Updates

- **2026-10-19 (M1a T1):** Kiota CLI 1.35.0 is pinned in the tool manifest, with the runtime
  `Microsoft.Kiota.Bundle` 2.1.2 (the newest releases at least two weeks old). The clients for
  BackOfficeApi and PublicApi live in `tooling/clients`; `tooling/clients/generate.sh` regenerates
  them from anywhere, recording paths relative to the repository, so local and CI output match.
  `.gitignore` excepts `tooling/clients/**/Publish/` from its rule for publish folders.
- **2026-10-19 (M1a U1):** The product's own frontends use Kiota too: the fan site has its own
  PublicApi client in `product/src/Frontends/FanSite/PublicApi`, because nothing in `product/` may
  use `tooling/` ([0021](0021-product-tooling-platform-separation.md)). `generate-client.sh` next to
  it regenerates it, and `product-ci` fails if it is out of date. So `Microsoft.Kiota.Bundle` is no
  longer tooling-only ([0049](0049-fan-site-blazor-server-tokens-on-the-server.md)).
