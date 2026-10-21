# Architecture decision records

One file per agreed decision. Proposed and Open items stay in
[`../design-decisions.md`](../design-decisions.md) until they are agreed; the overall picture
is in [`../architecture/overview.md`](../architecture/overview.md).

An agreed ADR is never rewritten. To change a decision, write a new ADR and mark the old one
**Superseded by NNNN**. Two things may be added in place: a status change, and a short dated
entry under **Updates** that settles something the ADR explicitly left open.

| ADR | Decision |
|---|---|
| [0001](0001-modular-monolith-with-several-hosts.md) | Modular monolith, deployed as several stateless hosts |
| [0002](0002-module-boundary-rules.md) | Module boundary rules: own schema, contracts only, no shared transactions |
| [0003](0003-core-platform-technologies.md) | PostgreSQL, Kafka, Valkey and Kubernetes as the core platform |
| [0004](0004-payment-providers-stripe-and-localpay.md) | Two payment providers: Stripe test mode and our LocalPay simulator |
| [0005](0005-no-paid-services-local-first.md) | No paid services; everything runs locally |
| [0006](0006-ticketing-modules.md) | The ten ticketing modules |
| [0007](0007-module-communication.md) | Synchronous calls only from Orders; events everywhere else |
| [0008](0008-hosts.md) | Seven deployables |
| [0009](0009-checkout-without-shared-transaction.md) | Checkout coordinated across modules without a shared transaction |
| [0010](0010-hold-lifetimes-and-payment-expiry.md) | Hold lifetimes; cancel the payment before releasing the seats |
| [0011](0011-seat-inventory-concurrency.md) | Seats are claimed with one conditional SQL statement |
| [0012](0012-build-our-own-waiting-room.md) | Build our own waiting room |
| [0013](0013-offline-first-check-in.md) | Offline-first check-in with signed tickets |
| [0014](0014-outbox-inbox-scheduler-on-postgresql.md) | Outbox, inbox and scheduler on PostgreSQL from M1; Kafka from M4 |
| [0015](0015-performance-targets.md) | Performance targets T1–T11 and how they are verified |
| [0016](0016-teaching-milestones.md) | Milestones mapped to modules, flows and targets |
| [0017](0017-scaling-lab-and-load-generator.md) | Scaling Lab and load generator |
| [0018](0018-capacity-from-configuration-not-code.md) | Lab capacity comes from configuration and the environment, never from code |
| [0019](0019-autoscaling-signals.md) | What scales on which signal |
| [0020](0020-local-kubernetes-on-docker-desktop.md) | Kubernetes in Docker Desktop for the lab, Aspire for development, kind in CI |
| [0021](0021-product-tooling-platform-separation.md) | Product, tooling and platform separated in one repository |
| [0022](0022-disposable-lab-environment.md) | Disposable lab environment |
| [0023](0023-single-tenant-organizer-scoped-data.md) | Single-tenant platform; organizers own their data |
| [0024](0024-domain-scope-m1-m6.md) | Domain and UI scope for M1–M6 |
| [0025](0025-object-storage-and-secrets.md) | Object storage and secrets |
| [0026](0026-module-project-layout.md) | Module project layout: four projects for rich modules, two for thin ones |
| [0027](0027-identity-provider-keycloak.md) | Keycloak as the identity provider |
| [0028](0028-gateway-and-browse-caching.md) | Gateway API with Traefik; browse caching in PublicApi |
| [0029](0029-container-image-per-host.md) | One container image per host, built by the .NET SDK |
| [0030](0030-runtime-and-api-style.md) | .NET 10 LTS, ASP.NET Core minimal APIs, built-in OpenAPI |
| [0031](0031-data-access-ef-core.md) | EF Core with Npgsql; hand-written SQL only where measured necessary |
| [0032](0032-patterns-inside-modules.md) | Patterns inside modules |
| [0033](0033-command-pipeline.md) | Our own command pipeline with decorators; no mediator |
| [0034](0034-test-stack.md) | Test stack |
| [0035](0035-blazor-for-all-uis.md) | Blazor for the user interfaces |
| [0036](0036-trunk-based-development.md) | Trunk-based development with pull-request checks |
| [0037](0037-kiota-api-clients.md) | Kiota for the tooling's API clients |
| [0038](0038-catalog-venues-seat-maps-and-event-lifecycle.md) | Catalog: venues, seat maps and the event lifecycle |
| [0039](0039-browsing-search-paging-and-caching.md) | Browsing: full-text search, paging and a versioned seat map |
| [0040](0040-organizers-in-m1a-and-others-data.md) | Organizers in M1a, and what callers learn about other people's data |
| [0041](0041-holds-sales-window-release-and-sweeper.md) | Holds: the sales window, one hold per fan, release, and the sweeper |
| [0042](0042-payment-provider-contract-and-webhooks.md) | The payment provider contract and webhook handling |
| [0043](0043-how-a-checkout-ends.md) | How a checkout ends: order states, the deadline, refunds and retries |
| [0044](0044-persistence-constraints-concurrency-unit-of-work.md) | Persistence rules: named constraints, optimistic concurrency, one unit of work per command |
| [0045](0045-recurring-messages-and-coordinating-handlers.md) | Scheduler: recurring messages and coordinating handlers |
| [0046](0046-localpay-scenarios-and-contract-checks.md) | LocalPay: misbehaviour by amount, and its contract checked in CI |
| [0047](0047-one-trace-per-purchase-and-metric-tags.md) | One trace per purchase; metrics from commands and business events |
| [0048](0048-test-layers-and-demo-dry-run.md) | Test layers: modules in their real hosts, and the demo as a system test |
| [0049](0049-fan-site-blazor-server-tokens-on-the-server.md) | The fan site: Blazor Interactive Server, with the fans' tokens on the server |
| [0050](0050-stripe-adapter-without-an-account.md) | The Stripe adapter, built and tested without a Stripe account |
| [0051](0051-signed-tickets-and-their-keys.md) | Signed tickets: JWS QR codes, a key per event, envelope encryption |
| [0052](0052-email-through-mailpit.md) | Email: SMTP with MailKit, Mailpit locally, Razor templates, a delivery log |
| [0053](0053-the-lab-cluster-addresses-identity-and-platform.md) | The lab cluster: addresses, identity and platform in M2 |

## Template

````markdown
# NNNN. Title

- **Status:** Agreed | Superseded by NNNN
- **Date:** YYYY-MM-DD
- **Related:** other ADRs, design-decisions.md sections

## Context
Why a decision is needed; facts and constraints.

## Decision
What we do.

## Alternatives considered
| Alternative | Why not |
|---|---|

## Consequences
What becomes easier, harder, or must now be done.
````
