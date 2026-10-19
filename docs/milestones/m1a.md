# M1a: modular monolith with the core purchase flow

- **Status:** Agreed 2026-10-18. The time-box is set at kickoff.
- **Related:** [ADR-0016](../adr/0016-teaching-milestones.md) (milestones), [architecture overview](../architecture/overview.md)

## Goal

One working purchase flow, run locally by Aspire, with correctness proven by tests in CI:
**browse → hold → checkout → pay with LocalPay → order confirmed, and hold expiry.**
No tickets, email or Stripe yet; those come in M1b. A first fan site was brought forward from
M1b on 2026-10-19 (U1).

## Scope

| In M1a | Not yet |
|---|---|
| Modules: Catalog, Organizers, Inventory, Orders, Payments (LocalPay adapter) | Stripe adapter, Ticketing, Notifications (M1b) |
| Hosts: PublicApi, BackOfficeApi, WebhookIngress, Workers, and the Migrator, all run by Aspire. The Migrator runs first; the other hosts wait for it to finish (`WaitForCompletion`) | Kubernetes, the lab, the Scaling Lab (M2) |
| Outbox, inbox, scheduled messages on PostgreSQL, built in-house ([ADR-0014](../adr/0014-outbox-inbox-scheduler-on-postgresql.md)) | Kafka (M4) |
| Keycloak in Aspire, realm imported from the repository, organizer memberships seeded in the realm | Organizers keeping Keycloak in step (M1b) |
| LocalPay simulator with the core scenarios | Timeouts and 500s, rate limits, sampled latencies, payment listing (M1b/M4) |
| Single refund for the "seats lost" case | Mass refunds, reconciliation job (M4) |
| Availability read directly from the database | Valkey caching and availability snapshots (M3) |
| Demos in the fan site (U1, brought forward on 2026-10-19), and through the API with `.http` files and Scalar | My tickets, my orders, the queue page; the back-office UI |

## Order of work

```
F1 skeleton ─► F2 shared ─► F3 hosts + AppHost ─► S1 WALKING SKELETON ─► S2 FIRST SLICE ─► breadth
                   └─► F4 outbox/inbox/scheduler ──────────────────────────┘     (C, I, O, P, L, Q in parallel)
F5 CI and F6 architecture tests run from S1 onward
```

- **S1, walking skeleton:** starting the AppHost brings everything up and every health check
  passes; CI is green. Nothing does business work yet.
- **S2, first vertical slice:** one seeded event goes hold → checkout → LocalPay "Approve" →
  webhook → order confirmed, end to end through the outbox. Kept thin: no limits, no expiry, no
  edge cases.
- **Breadth** follows in parallel streams. If time runs short, cut breadth, never the slice.

## Backlog

Sizes are for one developer: **S** ≤ 2 days, **M** ≤ 1 week, **L** > 1 week.

| ID | Item | Done when | Depends on | Size |
|---|---|---|---|---|
| F1 | Repository skeleton per [ADR-0021](../adr/0021-product-tooling-platform-separation.md): `product/`, `tooling/`, `dev/`; both solutions; shared build settings and central package versions; `.editorconfig`; nullable on, warnings as errors | Both solutions build | — | S |
| F2 | Shared building blocks: typed IDs, `Money`, `Result`; `ICommandHandler` and the decorator pipeline ([ADR-0033](../adr/0033-command-pipeline.md)); `IModule` registration with host kinds; integration-event envelope | Unit-tested; a sample command runs through every decorator in order | F1 | M |
| F3 | Hosts, ServiceDefaults (OpenTelemetry, health checks), AppHost wiring PostgreSQL, Keycloak and LocalPay; Migrator applying each module's migrations before the other hosts start | Starting the AppHost brings everything up healthy | F2 | M |
| F4 | Outbox, inbox and scheduled messages ([ADR-0014](../adr/0014-outbox-inbox-scheduler-on-postgresql.md)): tables in each module's schema; relay claiming rows with `SKIP LOCKED`; in-process delivery in Workers; trace context carried in message headers | Duplicate delivery is dropped; a crash between commit and publish loses nothing (tests) | F2 | L |
| F5 | CI: `product-ci` (build, unit, integration with Testcontainers, architecture tests, OpenAPI documents up to date) and `tooling-ci` (including generated clients up to date); then a ruleset on `main` requiring both | A pull request shows the checks; `main` is protected | F1 | S |
| F6 | Architecture tests (ArchUnitNET): module dependency table ([ADR-0006](../adr/0006-ticketing-modules.md)), project layering ([ADR-0026](../adr/0026-module-project-layout.md)), product never references tooling | Breaking a rule fails CI | F2 | S |
| K1 | Keycloak: realm export in the repository (clients, roles, 2 fans, 1 organizer member with a seeded membership, 1 admin); JWT validation in the hosts; a test-token helper for integration tests | Calls without a token get 401; tests run without Keycloak | F3 | M |
| S1 | **Walking skeleton** checkpoint | See above | F1–F3, F5 | — |
| S2 | **First vertical slice**, built across the teams | See above, demonstrated | S1, F4, minimal parts of I1, O1, P1, L1 | L |
| C1 | Catalog: venues with seat-map import (sections, rows, seats, standing slots, price category per seat); events; price categories; on-sale schedule; publishing (`EventPublished`, `PricesChanged`, `OnSaleScheduled`); lifecycle state machine | A 2,000-seat venue imports and publishes | F4 | M |
| C2 | Browse endpoints in PublicApi: event list with PostgreSQL full-text search, event detail, seat-map geometry under a versioned URL | Covered by `.http` files and tests | C1 | S |
| G1 | Organizers (thin): organizers and members; organizer context from token claims feeding the context decorator | An organizer can't touch another organizer's events (test) | K1 | S |
| I1 | Inventory: seat and slot rows created from `EventPublished`; the claim SQL from [ADR-0011](../adr/0011-seat-inventory-concurrency.md) with expiry checked inside it; one active hold per fan per event; maximum seats per hold; best available in a section; availability endpoint | Claims are all or nothing; an expired seat can be claimed immediately (tests) | C1, F4 | L |
| I2 | Inventory module API: `BeginCheckout`, `ConfirmHold`, `ReleaseHold`, all safe to repeat; backstop expiry; sweeper running on the scheduler | Contract tests | I1 | M |
| O1 | Orders: local price list built from Catalog events; `StartCheckout` coordinator ([ADR-0009](../adr/0009-checkout-without-shared-transaction.md)); order state machine; at the deadline, cancel the payment first and release the seats second; on `AlreadyCompleted`, ask the provider for the status if no webhook arrives within 2 minutes | Expiry and deadline flows pass their tests | I2, P1 | L |
| O2 | Orders: handle `PaymentSucceeded` → `ConfirmHold` → Confirmed, or seats lost → refund; order-status endpoint for the return page | Tested, including the seats-lost path | O1 | M |
| P1 | Payments: `IPaymentProvider` including `CancelPaymentAsync` and `ListPaymentsAsync`; LocalPay adapter; `CreateCheckout` and `CancelCheckout`; WebhookIngress checks the HMAC signature and stores in the inbox; `PaymentSucceeded` and `PaymentFailed` | Duplicate and out-of-order webhooks are handled (tests) | F4, L1 | L |
| P2 | Single refund through LocalPay; `RefundSucceeded` and `RefundFailed` | The seats-lost case ends Refunded | P1 | S |
| L1 | LocalPay simulator (tooling): sessions, hosted Approve/Decline page, redirect, HMAC-signed webhooks, early expiry, status, refund, headless auto-approve. Scenarios: decline, duplicate webhook, delayed webhook, missing webhook, payment completing during expiry | Runs in Aspire; its API is described by an OpenAPI document | F1 | L |
| T1 | Demo seeder (`tooling/seed`) using Kiota clients generated from the product's OpenAPI documents ([ADR-0037](../adr/0037-kiota-api-clients.md)): imports the venue, creates, publishes and opens an event | One command prepares the demo | C1, F5 | S |
| Q1 | Concurrency test (target T2 for M1a): 500 parallel hold attempts on 100 seats, with overlapping requests | Exactly 100 seats held; no seat in two holds; after paying, every Confirmed order owns its seats and every seats-lost order ends Refunded, checked through module APIs | I1, O2, P2 | M |
| Q2 | Crash-point and idempotency tests: the rows of ADR-0009's crash table that apply in M1a; repeated `POST /holds` and `POST /orders`; duplicate webhooks | All pass in CI | O2, P1, L1 | M |
| Q3 | Observability: one trace from the HTTP request through the outbox, Workers and the consumer; first metrics (hold results, checkout outcomes) and a metrics catalogue in `docs/` | The trace shows in the Aspire dashboard | F4 | S |
| Q4 | Demo script, `.http` files, Scalar | A dry run of the demo passes | all | S |
| U1 | Fan site, brought forward from M1b on 2026-10-19 ([ADR-0049](../adr/0049-fan-site-blazor-server-tokens-on-the-server.md)): Blazor Interactive Server; sign-in at Keycloak with tokens kept on the server; event list and search; seat map with picking and best available; hold with countdown, release, checkout; the return page following the order | The demo runs in the browser; system tests load the pages and sign in through Keycloak | C2, I1, O2, K1 | M |

**Splitting the work:** one or two people build F1–F3 and S1 first. After S1, five streams run
in parallel:

1. Catalog, Organizers and the seeder.
2. Inventory.
3. Orders.
4. Payments and LocalPay.
5. Foundation, CI and identity.

F4 is on the critical path, so give it to the strongest developer early.

## Definition of done

1. On a clean machine with only the .NET SDK and Docker Desktop, `dotnet run --project dev/AppHost`
   brings everything up healthy.
2. The demo script passes end to end.
3. `product-ci` and `tooling-ci` are green, and `main` requires both.
4. Unit, integration (real PostgreSQL), architecture, concurrency (Q1) and crash-point (Q2)
   tests all pass.
5. One trace shows the whole purchase in the Aspire dashboard.
6. OpenAPI documents and generated clients are committed and current; the metrics catalogue
   exists.
7. Any new decision has an ADR.

## Demo script (about 15 minutes)

1. Start the AppHost and show the dashboard: PostgreSQL, Keycloak, LocalPay, the Migrator having
   finished, four hosts running.
2. Run the seeder: a 2,000-seat venue and an event that's on sale.
3. Browse in the fan site: event list, search, event page with the seat map.
4. Fan A holds 2 seats. Fan B, in another browser, tries the same seats and is refused at once
   (409).
5. Fan A checks out, approves on the LocalPay page, and comes back to the order page, which goes
   Pending → Confirmed.
6. Show the single trace: hold → order → webhook → outbox → Workers → `ConfirmHold`.
7. Expiry, with demo settings of a 1-minute hold and a 2-minute checkout: an abandoned hold is
   claimable the moment it expires. An abandoned checkout has its LocalPay session expired
   first, then its seats released.
8. Failures: a duplicate webhook confirms the order once. A payment completing during expiry
   still ends consistent.
9. CI: a pull request with its required checks.

## Risks

- **The outbox and the checkout coordinator are the hard parts and sit on the critical path.**
  S2 forces them early, on purpose.
- **Keycloak configuration can eat days.** The realm export is committed, and test tokens keep
  tests independent of Keycloak.
- **M1b work creeping in** (tickets, Stripe): the scope table is the referee. The fan site was
  brought forward on purpose (U1).
- **Team size is unknown.** Set the time-box at kickoff, and cut breadth before the slice.
