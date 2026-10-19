# 0048. Test layers: modules in their real hosts, and the demo as a system test

- **Status:** Agreed
- **Date:** 2026-10-19
- **Related:** [0009](0009-checkout-without-shared-transaction.md) · [0015](0015-performance-targets.md) · [0021](0021-product-tooling-platform-separation.md) · [0034](0034-test-stack.md) · [0046](0046-localpay-scenarios-and-contract-checks.md) · design-decisions §14 (C1-j, P1-g, Q1-a, Q2-b…c, Q4-a…b, T1-c)

## Context

[0034](0034-test-stack.md) chose the tools (xUnit, Testcontainers, ArchUnitNET, test tokens).
M1a settled how the layers fit together, and showed what each layer can and can't catch.

## Decision

| Layer | What runs | What it proves |
|---|---|---|
| Unit and architecture | In process | The pipeline's order, module rules, layering |
| Shared integration | The outbox, inbox and scheduler with two small test modules, on PostgreSQL | Delivery, retries, idempotency, recurring messages |
| **Module integration** (`product/tests/Modules.IntegrationTests`) | The real PublicApi, BackOfficeApi and WebhookIngress in memory, one migrated PostgreSQL, test tokens. The Workers host is built but not started: tests step the outbox relay and scheduler themselves. All outgoing HTTP goes to a fake LocalPay built from its published API | Business flows, races, crash points, T2 |
| **System** (`dev/AppHost.Tests`) | The whole AppHost in Docker, real Keycloak and LocalPay, in demo mode (1-minute holds, 2-minute checkouts) | Wiring, configuration, and the demo script itself |

- Module tests share one database and stay apart by using their own organizers, events and
  fans. Delivery waits until nothing due is pending, even messages another test is delivering.
  The sweeper runs only when a test makes it due.
- **Crash points** are reproduced by stopping the flow where the crash would (calling a module
  API step directly, or making LocalPay fail or lose its answer for one price), then letting
  recovery run ([0009](0009-checkout-without-shared-transaction.md)).
- **The T2 test** runs on every build: 500 overlapping holds on 100 seats, then payment with
  some seats lost, then the invariants ([0015](0015-performance-targets.md)).
- **The demo's dry run is a system test**: the steps of the demo script, including the timed
  expiry steps, run on every pull request.
- Important tests get a **mutation check** when written: break the code they guard and see
  them fail.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Module tests calling handlers directly | Skips the HTTP surface, authentication and the real composition |
| Module tests against the real LocalPay | Product tests may not reference tooling ([0021](0021-product-tooling-platform-separation.md)) |
| Running the background services in module tests | Message timing becomes nondeterministic |
| A manual dry run before each demo | Goes stale; the system test keeps "the demo works" true |

## Consequences

- The system tests catch what the faked layers can't: in M1a they found that Workers had no
  route to LocalPay, and that Keycloak switched to HTTPS on `dotnet run`.
- The AppHost CI job takes a few minutes longer for the timed demo steps.
