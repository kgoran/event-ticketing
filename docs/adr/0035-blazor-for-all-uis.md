# 0035. Blazor for the user interfaces

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0013](0013-offline-first-check-in.md) · [0024](0024-domain-scope-m1-m6.md) · [0030](0030-runtime-and-api-style.md) · design-decisions §9

## Context

The earlier proposal was Next.js for the fan site and Blazor for the back office. The UI scope
is now minimal ([0024](0024-domain-scope-m1-m6.md)), and the project is for a .NET team.

## Decision

- **Blazor** (Blazor Web App) for the fan site and the back office: one UI stack, in the
  team's language.
- **Scanner app:** an offline-capable web app ([0013](0013-offline-first-check-in.md)). The
  lean is Blazor WebAssembly as a PWA, with a small JavaScript interop for camera-based QR
  decoding; settled in M3 when CheckIn is built.
- **M1a has no UI.** Flows are demonstrated through the API with `.http` files and Scalar
  ([0030](0030-runtime-and-api-style.md)); the fan UI follows once the API flows exist.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Next.js for the fan site | A second stack (Node, React, its own build and deploy) costs a .NET team more than it teaches; its search-engine benefits don't matter for a teaching project |
| Another SPA framework | Same reason |

## Consequences

- One language end to end.
- Front-end performance and SEO are not teaching goals.
- Camera access in the scanner needs JavaScript interop.
