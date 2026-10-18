# 0034. Test stack

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0002](0002-module-boundary-rules.md) · [0006](0006-ticketing-modules.md) · [0015](0015-performance-targets.md) · [0021](0021-product-tooling-platform-separation.md) · [0026](0026-module-project-layout.md) · [0027](0027-identity-provider-keycloak.md) · design-decisions §7, §11

## Context

The tests must run against the real database (behaviour like `xmin`, `SKIP LOCKED` and
constraints matters), enforce the module rules, and use only free libraries
([0005](0005-no-paid-services-local-first.md)).

## Decision

| Need | Choice |
|---|---|
| Test framework | xUnit v3 |
| Database in integration tests | Real PostgreSQL through Testcontainers; no in-memory database provider |
| Test doubles | NSubstitute |
| Assertions | AwesomeAssertions: an Apache 2.0 fork that keeps FluentAssertions' syntax (FluentAssertions itself is commercial since v8) |
| Architecture tests | ArchUnitNET: the module rules ([0002](0002-module-boundary-rules.md)), the dependency table ([0006](0006-ticketing-modules.md)), project layering ([0026](0026-module-project-layout.md)) and the product/tooling separation ([0021](0021-product-tooling-platform-separation.md)) |
| Authentication in tests | Tokens signed with a local test key in integration tests; the real Keycloak in system tests ([0027](0027-identity-provider-keycloak.md)) |

A concurrency test that places many holds in parallel and then runs the invariant checks (T2,
[0015](0015-performance-targets.md)) is part of CI from M1.

## Alternatives considered

| Alternative | Why not |
|---|---|
| NUnit or MSTest | Both fine; xUnit is the most common in .NET open source and runs tests in parallel by default |
| EF Core InMemory or SQLite in tests | Behave differently from PostgreSQL exactly where it matters |
| FluentAssertions v8 | Commercial licence |
| Shouldly | Free, but a different syntax from what the team knows |
| NetArchTest | Simpler, but ArchUnitNET is more expressive and actively maintained |

## Consequences

- A container engine is needed to run integration tests locally (covered by
  [0020](0020-local-kubernetes-on-docker-desktop.md)).
- Integration tests are slower than in-memory ones; containers are shared per test collection
  to keep them fast.

## Updates

- **2026-10-18:** Tests run on Microsoft.Testing.Platform, switched on in `global.json`. xUnit
  v3 4.x needs it with the .NET 10 SDK; the VSTest packages (`Microsoft.NET.Test.Sdk`,
  `xunit.runner.visualstudio`) are not used. Commands: `dotnet test --solution <slnx>` or
  `dotnet test --project <csproj>`.
