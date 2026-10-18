# 0030. .NET 10 LTS, ASP.NET Core minimal APIs, built-in OpenAPI

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0001](0001-modular-monolith-with-several-hosts.md) · [0008](0008-hosts.md) · [0021](0021-product-tooling-platform-separation.md) · design-decisions §9

## Context

The product and the tooling need one runtime and one way of exposing HTTP endpoints. Tooling
depends on the product's APIs through OpenAPI documents
([0021](0021-product-tooling-platform-separation.md)).

## Decision

- **.NET 10 LTS** for all product and tooling code.
- **ASP.NET Core minimal APIs.** Each module maps its own endpoints through
  `IModule.MapEndpoints(app, hostKind)`, so each host exposes only its modules' endpoints
  ([0008](0008-hosts.md)).
- **Built-in OpenAPI document generation** (`Microsoft.AspNetCore.OpenApi`) for each public
  host. These documents are the contract the tooling builds its clients from.
- **Scalar** as the interactive API reference in development; `.http` files for repeatable
  manual calls.
- **gRPC only if a module is ever extracted** ([0001](0001-modular-monolith-with-several-hosts.md));
  inside the monolith, module APIs are plain in-process interfaces.

## Alternatives considered

| Alternative | Why not |
|---|---|
| MVC controllers | More ceremony; minimal APIs fit feature-folder vertical slices |
| Swashbuckle | OpenAPI generation is now built into ASP.NET Core |
| gRPC between modules now | There is no network boundary to cross yet |

## Consequences

- One LTS runtime across product and tooling.
- The OpenAPI documents are generated at build time and kept in the repository, so a contract
  change shows up in the pull request diff.
