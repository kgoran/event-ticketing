# Design decisions (log)

The status of every architecture and stack decision. Agreed items have an ADR in
[`adr/`](adr/README.md); this log keeps Proposed and Open items and points to the ADRs. The
overall picture is in [`architecture/overview.md`](architecture/overview.md).

**Status:** **Agreed** = confirmed by the project owner (has an ADR) · **Proposed** =
recommended, no objection raised, not explicitly confirmed · **Open** = needs a decision.

---

## Agreed in the architecture pass (2026-10-18)

| ADR | Decision |
|---|---|
| [0001](adr/0001-modular-monolith-with-several-hosts.md) | Modular monolith, deployed as several stateless hosts |
| [0002](adr/0002-module-boundary-rules.md) | Module boundary rules |
| [0003](adr/0003-core-platform-technologies.md) | PostgreSQL, Kafka, Valkey, Kubernetes |
| [0004](adr/0004-payment-providers-stripe-and-localpay.md) | Stripe test mode + LocalPay |
| [0005](adr/0005-no-paid-services-local-first.md) | No paid services; everything runs locally |
| [0006](adr/0006-ticketing-modules.md) | Ten ticketing modules |
| [0007](adr/0007-module-communication.md) | Synchronous calls only from Orders; events elsewhere |
| [0008](adr/0008-hosts.md) | Seven deployables |
| [0009](adr/0009-checkout-without-shared-transaction.md) | Checkout without a shared transaction |
| [0010](adr/0010-hold-lifetimes-and-payment-expiry.md) | Hold lifetimes; cancel payment before releasing seats |
| [0011](adr/0011-seat-inventory-concurrency.md) | Conditional single-statement seat claims |
| [0012](adr/0012-build-our-own-waiting-room.md) | Own waiting room |
| [0013](adr/0013-offline-first-check-in.md) | Offline-first check-in |
| [0014](adr/0014-outbox-inbox-scheduler-on-postgresql.md) | Outbox, inbox, scheduler from M1; Kafka from M4 |
| [0015](adr/0015-performance-targets.md) | Performance targets T1–T11 |
| [0016](adr/0016-teaching-milestones.md) | Milestone mapping |
| [0017](adr/0017-scaling-lab-and-load-generator.md) | Scaling Lab and load generator |
| [0018](adr/0018-capacity-from-configuration-not-code.md) | Lab capacity from configuration, not code |
| [0019](adr/0019-autoscaling-signals.md) | Autoscaling signals |
| [0020](adr/0020-local-kubernetes-on-docker-desktop.md) | Docker Desktop Kubernetes, Aspire, kind |
| [0021](adr/0021-product-tooling-platform-separation.md) | Product / tooling / platform separation |
| [0022](adr/0022-disposable-lab-environment.md) | Disposable lab environment |
| [0023](adr/0023-single-tenant-organizer-scoped-data.md) | Single-tenant, organizer-scoped data |
| [0024](adr/0024-domain-scope-m1-m6.md) | Domain and UI scope for M1–M6 |
| [0025](adr/0025-object-storage-and-secrets.md) | Object storage and secrets |
| [0026](adr/0026-module-project-layout.md) | Module project layout (hybrid) |
| [0027](adr/0027-identity-provider-keycloak.md) | Keycloak as identity provider |
| [0028](adr/0028-gateway-and-browse-caching.md) | Gateway API with Traefik; browse caching in PublicApi |
| [0029](adr/0029-container-image-per-host.md) | One container image per host |
| [0030](adr/0030-runtime-and-api-style.md) | .NET 10 LTS, minimal APIs, built-in OpenAPI |
| [0031](adr/0031-data-access-ef-core.md) | EF Core with Npgsql; migrations per module |
| [0032](adr/0032-patterns-inside-modules.md) | Patterns inside modules |
| [0033](adr/0033-command-pipeline.md) | Own command pipeline with decorators; no mediator |
| [0034](adr/0034-test-stack.md) | Test stack |
| [0035](adr/0035-blazor-for-all-uis.md) | Blazor for the user interfaces |
| [0036](adr/0036-trunk-based-development.md) | Trunk-based development with pull-request checks |
| [0037](adr/0037-kiota-api-clients.md) | Kiota for the tooling's API clients |

---

## 1. Architecture style — Agreed

Modular monolith, deployed as several stateless hosts:
[ADR-0001](adr/0001-modular-monolith-with-several-hosts.md). Hosts: `PublicApi`,
`WaitingRoom`, `WebhookIngress`, `BackOfficeApi`, `CheckInApi`, `Workers`, `Migrator`
([ADR-0008](adr/0008-hosts.md)).

## 2. Module boundaries — Agreed

Rules: [ADR-0002](adr/0002-module-boundary-rules.md). Module list:
[ADR-0006](adr/0006-ticketing-modules.md). Communication:
[ADR-0007](adr/0007-module-communication.md).

## 3. Module internal structure

**Agreed — hybrid layout:** [ADR-0026](adr/0026-module-project-layout.md).

- **Option A, Clean Architecture per module** (`X.Domain`, `X.Application`,
  `X.Infrastructure` — which also holds HTTP endpoints and module registration — and
  `X.Contracts`) for the modules with a full domain model: Inventory, Orders, Payments,
  Ticketing, CheckIn.
- **Option B, two projects** (`X.Contracts` + `X`, layers as folders) for the thin modules:
  Catalog, Organizers, Notifications, Reporting, WaitingRoom.
- Shared: `Shared.Domain`, `Shared.Application`, `Shared.Infrastructure`, `Shared.Contracts`.
  Reference rules and guardrails for the Shared projects are in the ADR.

**Agreed — what we keep from Clean Architecture, what we drop, and feature folders:**
[ADR-0032](adr/0032-patterns-inside-modules.md).

## 4. Patterns inside modules — Agreed

Value objects, small aggregates with `xmin`, state machines, process managers, pluggable
policies, Result types, lightweight CQRS; specification pattern only where it earns its place:
[ADR-0032](adr/0032-patterns-inside-modules.md). The hold path's single-statement claim is the
agreed exception to optimistic concurrency ([ADR-0011](adr/0011-seat-inventory-concurrency.md)).

## 5. Cross-cutting

- **Agreed — command pipeline** (own `ICommandHandler`, decorators via Scrutor, audit at the
  command level, FluentValidation, no mediator): [ADR-0033](adr/0033-command-pipeline.md).
- **Agreed — outbox, inbox and scheduler built in-house:**
  [ADR-0014](adr/0014-outbox-inbox-scheduler-on-postgresql.md) (update).
- **Proposed — resilience** via `Microsoft.Extensions.Resilience`: timeouts and circuit
  breakers on all outbound calls, a concurrency limit per external provider, retries only for
  calls that are safe to repeat.
- **Proposed — licensing check:** MediatR, MassTransit v9, AutoMapper and FluentAssertions
  became commercial in 2025 — avoid. Also check the PDF library: QuestPDF's free licence has a
  company-revenue threshold (verify).

## 6. Patterns to avoid — Proposed

- A generic repository over EF Core.
- A shared "Common" project holding business entities.
- A mediator for calls between modules (hides dependencies).
- One module reading another module's tables; a shared DbContext.
- Chains of synchronous calls across modules (use events or a local read model).
- Event sourcing outside a ledger; distributed transactions (two-phase commit).
- External calls (payment provider, email) inside a database transaction.

## 7. Enforcing the rules — Proposed

- *Agreed:* architecture tests in CI with ArchUnitNET ([ADR-0034](adr/0034-test-stack.md)).
- A separate database login per module schema.
- Each module registers itself via `IModule.Register(services, config)` and
  `MapEndpoints(app, hostKind)`; each host picks the modules and endpoints it runs.
- *Agreed:* a CI check that nothing under `product/` references `tooling/`
  ([ADR-0021](adr/0021-product-tooling-platform-separation.md)).

## 8. Scaling model — Proposed

- **Stateless hosts**, each a separate deployment with its own autoscaling rule
  (signals Agreed in [ADR-0019](adr/0019-autoscaling-signals.md)). Minimum one replica per
  zone on critical paths; locally, zones are three labelled Kubernetes nodes and Karpenter is
  not used ([ADR-0005](adr/0005-no-paid-services-local-first.md)). Graceful shutdown;
  idempotency makes retried requests safe.
- **One module can scale separately without becoming a service:** the same image deployed
  with only some modules' consumers switched on (e.g. `workers-notifications`).
- **The database writer is the ceiling for writes.** Levers, in order: connection pooling
  (PgBouncer) → caching → read replicas (never for decisions on inventory or money) →
  analytics moved off PostgreSQL → separate clusters per module (possible because of
  schema-per-module) → a bigger writer → partitioning the data across several writers
  ("cells").
- **Keep write transactions short:** one database round-trip where possible, no external
  calls inside a transaction, only critical work synchronous (everything else via outbox).
- **Kafka:** partitions set parallelism; key by the entity that needs ordering; choose a
  generous partition count up front (adding partitions later remaps keys).
- **Load shedding:** under overload, reject new work quickly instead of queueing until
  clients time out and retry. Also the basis of the lab's capacity limits
  ([ADR-0018](adr/0018-capacity-from-configuration-not-code.md)).
- **Ticketing-specific — Agreed:** a single hot event lands on one writer, so cells don't
  help it. Handled by conditional seat claims and admission control
  ([ADR-0011](adr/0011-seat-inventory-concurrency.md),
  [ADR-0012](adr/0012-build-our-own-waiting-room.md)).

## 9. Technology stack

| Area | Choice | Status |
|---|---|---|
| Backend | .NET 10 LTS, ASP.NET Core minimal APIs, built-in OpenAPI; gRPC only if a module is extracted | Agreed — [ADR-0030](adr/0030-runtime-and-api-style.md) |
| Database | PostgreSQL with the CloudNativePG operator | Agreed — [ADR-0003](adr/0003-core-platform-technologies.md). Aurora dropped ([ADR-0005](adr/0005-no-paid-services-local-first.md)) |
| Data access | EF Core with Npgsql; hand-written SQL only where measured necessary (today: the hold path) | Agreed — [ADR-0031](adr/0031-data-access-ef-core.md), [ADR-0011](adr/0011-seat-inventory-concurrency.md) |
| Cache | Valkey; never authoritative for inventory or money | Agreed — [ADR-0003](adr/0003-core-platform-technologies.md) |
| Messaging | Kafka protocol (Redpanda locally) + transactional outbox, Protobuf schemas | Agreed — [ADR-0003](adr/0003-core-platform-technologies.md), [ADR-0014](adr/0014-outbox-inbox-scheduler-on-postgresql.md) |
| Compute | Kubernetes + KEDA; Kubernetes in Docker Desktop for the lab, kind in CI | Agreed — [ADR-0003](adr/0003-core-platform-technologies.md), [ADR-0019](adr/0019-autoscaling-signals.md), [ADR-0020](adr/0020-local-kubernetes-on-docker-desktop.md). EKS, Karpenter and ECS Fargate dropped ([ADR-0005](adr/0005-no-paid-services-local-first.md)) |
| Edge | Cloudflare (WAF, DDoS, bot management) | Dropped — no paid services; a caching layer in front of PublicApi stands in ([ADR-0005](adr/0005-no-paid-services-local-first.md)) |
| Frontend | Blazor for the fan site and back office; scanner as an offline-capable PWA (lean: Blazor WebAssembly) | Agreed — [ADR-0035](adr/0035-blazor-for-all-uis.md); scope [ADR-0024](adr/0024-domain-scope-m1-m6.md). Next.js dropped |
| Identity | Keycloak, standard OIDC only in the product | Agreed — [ADR-0027](adr/0027-identity-provider-keycloak.md) |
| Gateway | Kubernetes Gateway API; Traefik in the lab | Agreed — [ADR-0028](adr/0028-gateway-and-browse-caching.md) |
| Browse caching | ASP.NET Core output caching in PublicApi, stored in Valkey | Agreed — [ADR-0028](adr/0028-gateway-and-browse-caching.md) |
| Container images | One per host, .NET SDK container publishing, chiseled base images | Agreed — [ADR-0029](adr/0029-container-image-per-host.md) |
| Observability | OpenTelemetry → Grafana stack (Prometheus, Loki, Tempo, Grafana) | Agreed — required by [ADR-0017](adr/0017-scaling-lab-and-load-generator.md) and [ADR-0019](adr/0019-autoscaling-signals.md) |
| Object storage | S3 API; SeaweedFS locally | Agreed — [ADR-0025](adr/0025-object-storage-and-secrets.md) |
| Secrets | .NET user-secrets (dev), Kubernetes Secrets (lab), GitHub Actions secrets (CI) | Agreed — [ADR-0025](adr/0025-object-storage-and-secrets.md) |
| Way of working | Trunk-based development, pull-request checks | Agreed — [ADR-0036](adr/0036-trunk-based-development.md) |
| API clients (tooling, and the product's frontends) | Kiota, generated from the committed OpenAPI documents | Agreed — [ADR-0037](adr/0037-kiota-api-clients.md); the fan site's client: [ADR-0049](adr/0049-fan-site-blazor-server-tokens-on-the-server.md) |
| IaC / CI/CD | GitHub Actions (within free minutes), Argo CD; Terraform only if a cloud environment is ever added | Proposed |
| Analytics | Debezium (CDC) → ClickHouse | Proposed (later milestone) |
| Cloud | None | Agreed — [ADR-0005](adr/0005-no-paid-services-local-first.md) |

## 10. Availability and recovery targets — Proposed (revisit for ticketing)

| Scenario | RPO (max data loss) | RTO (max downtime) |
|---|---|---|
| Instance or pod failure | 0 | seconds, automatic |
| Availability-zone failure | 0 (synchronous standby) | < 5 min, automatic |
| Region failure | < 1 min (async cross-region replica) | 1–2 h, warm standby, failover approved by a person |
| Corrupted data, bad migration, ransomware | ≤ 5 min (point-in-time restore) | ≤ 4 h |

Regional failover is a human decision to avoid two regions accepting writes (split brain).

*Effect of [ADR-0005](adr/0005-no-paid-services-local-first.md):* there is no second region,
so the region row can't be demonstrated. A zone failure is simulated by stopping one of the
three lab nodes; the point-in-time restore drill runs locally against S3-compatible storage.

## 11. Local development and testing

| Item | Status |
|---|---|
| **Aspire** runs the product locally for day-to-day development (PostgreSQL, Redpanda, Valkey, Mailpit, LocalPay) | Agreed — [ADR-0020](adr/0020-local-kubernetes-on-docker-desktop.md) |
| **Lab environment:** Kubernetes in Docker Desktop (kind mode, 3 nodes) with CloudNativePG, rebuilt on every start | Agreed — [ADR-0020](adr/0020-local-kubernetes-on-docker-desktop.md), [ADR-0022](adr/0022-disposable-lab-environment.md) |
| **kind** in CI for system tests (nightly) | Agreed — [ADR-0020](adr/0020-local-kubernetes-on-docker-desktop.md) |
| **Testcontainers** for integration tests against real PostgreSQL | Agreed — [ADR-0034](adr/0034-test-stack.md) |
| **Toxiproxy** to inject latency and failures | Agreed — [ADR-0018](adr/0018-capacity-from-configuration-not-code.md) |
| **Stripe CLI** to forward webhooks to a laptop; **Mailpit** to catch email | Proposed |
| **k6** load tests against the performance targets | Agreed — [ADR-0015](adr/0015-performance-targets.md) |
| **Scaling Lab** and load generator | Agreed — [ADR-0017](adr/0017-scaling-lab-and-load-generator.md) |
| **Architecture tests** in CI (ArchUnitNET) | Agreed — [ADR-0034](adr/0034-test-stack.md) |
| Per module: one unit-test project and one integration-test project | Proposed |

## 12. Repository layout — Agreed

Product, tooling and platform in one repository:
[ADR-0021](adr/0021-product-tooling-platform-separation.md). This replaces the earlier draft,
which put `tools/` and `tests/LoadTests` inside the product solution. The project structure
inside `product/src/Modules/` follows [ADR-0026](adr/0026-module-project-layout.md).

## 13. Open questions

No open design questions.

Verification spikes (the decision is made; the spike confirms the mechanism works):

| Milestone | Spike | Fallback |
|---|---|---|
| M3 | CloudNativePG backups and restores against SeaweedFS ([ADR-0025](adr/0025-object-storage-and-secrets.md)) | RustFS |
| M3 | Scanner device credentials as Keycloak clients ([ADR-0027](adr/0027-identity-provider-keycloak.md)) | Device tokens issued by CheckIn |
| M5 | KEDA scaling CloudNativePG read replicas ([ADR-0019](adr/0019-autoscaling-signals.md)) | Small lab controller that changes the instance count |

Done: M2's spike, locally built images reaching the lab cluster: `kind load` works, no registry
needed (2026-10-21, [ADR-0029](adr/0029-container-image-per-host.md) update).

Closed:

| # | Question | Answer |
|---|---|---|
| 1 | Module layout | Hybrid — [ADR-0026](adr/0026-module-project-layout.md) |
| 2 | Module list and boundaries | [ADR-0006](adr/0006-ticketing-modules.md), [ADR-0007](adr/0007-module-communication.md) |
| 3 | Seat inventory concurrency and hot events | [ADR-0011](adr/0011-seat-inventory-concurrency.md) |
| 4 | Waiting room: build or buy | Build — [ADR-0012](adr/0012-build-our-own-waiting-room.md) |
| 5 | Hold duration vs Stripe's minimum session expiry (verified: 30 min) | Our own policy; expire the session at our deadline — [ADR-0010](adr/0010-hold-lifetimes-and-payment-expiry.md) |
| 6 | Cloud vs local-first | No paid services; local only — [ADR-0005](adr/0005-no-paid-services-local-first.md) |
| 7 | Multi-tenancy | Single-tenant, organizer-scoped data — [ADR-0023](adr/0023-single-tenant-organizer-scoped-data.md) |
| 8 | Frontend scope | Minimal UIs — [ADR-0024](adr/0024-domain-scope-m1-m6.md) (fan-site technology still Proposed, §9) |
| 9 | Identity provider | Keycloak — [ADR-0027](adr/0027-identity-provider-keycloak.md) |
| 10 | Team size and milestone pacing | M1 split into M1a/M1b; each milestone time-boxed at its kickoff — [ADR-0016](adr/0016-teaching-milestones.md) (update) |
| 11 | Organizer API with outbound webhooks | Out of M1–M6 — [ADR-0024](adr/0024-domain-scope-m1-m6.md) |
| 12 | Docker Desktop licence | The free plan applies: the company has fewer than 250 employees, and annual revenue under $10M is assumed. Fallback: Podman Desktop with kind — [ADR-0020](adr/0020-local-kubernetes-on-docker-desktop.md) (update) |
| 13 | S3-compatible server | SeaweedFS — [ADR-0025](adr/0025-object-storage-and-secrets.md) |
| 14 | Gateway, and where browse caching lives | Gateway API with Traefik; output caching in PublicApi — [ADR-0028](adr/0028-gateway-and-browse-caching.md) |
| 15 | Container images | One per host — [ADR-0029](adr/0029-container-image-per-host.md) |
| 16 | KEDA scaling CloudNativePG replicas | Approach agreed in [ADR-0019](adr/0019-autoscaling-signals.md); verified by the M5 spike above |
| 17 | Where the repository lives; public or private | Public: [github.com/kgoran/event-ticketing](https://github.com/kgoran/event-ticketing). Required checks can be enforced on the free plan — [ADR-0036](adr/0036-trunk-based-development.md) (update) |

## 14. Decided during M1a implementation — Agreed 2026-10-19

Implementation-level choices made while building M1a, where neither an ADR nor the plan
said. All were agreed on 2026-10-19. Those with lasting weight are recorded as ADRs; the rest
stay here as the record.

| ADR | Rows |
|---|---|
| [0038](adr/0038-catalog-venues-seat-maps-and-event-lifecycle.md) Catalog: venues, seat maps, lifecycle | C1-a…e, C1-g, C1-h, C2-b |
| [0039](adr/0039-browsing-search-paging-and-caching.md) Browsing | C2-a, C2-c…f |
| [0040](adr/0040-organizers-in-m1a-and-others-data.md) Organizers; other people's data | G1-a…c, C1-i |
| [0041](adr/0041-holds-sales-window-release-and-sweeper.md) Holds and the sweeper | I1-a…g, I2-b…e |
| [0042](adr/0042-payment-provider-contract-and-webhooks.md) Payment provider contract and webhooks | P1-a…e, P2-b…d, S2-d |
| [0043](adr/0043-how-a-checkout-ends.md) How a checkout ends | O1-a, O1-c…e, O2-a…d, Q2-a |
| [0044](adr/0044-persistence-constraints-concurrency-unit-of-work.md) Persistence rules | I1-d (mechanism), I1-h, P1-f, O1-f |
| [0045](adr/0045-recurring-messages-and-coordinating-handlers.md) Recurring messages, coordinating handlers | I2-a, O1-b, O2-b |
| [0046](adr/0046-localpay-scenarios-and-contract-checks.md) LocalPay | L1-a…f |
| [0047](adr/0047-one-trace-per-purchase-and-metric-tags.md) Observability | Q3-a…c |
| [0048](adr/0048-test-layers-and-demo-dry-run.md) Test layers and the demo dry run | C1-j, P1-g, Q1-a, Q2-b, Q2-c, Q4-a, Q4-b, T1-c |
| [0049](adr/0049-fan-site-blazor-server-tokens-on-the-server.md) The fan site (U1, brought forward from M1b) | D1–D5, agreed with the owner on 2026-10-19; implementation choices in the ADR |
| Updates to [0007](adr/0007-module-communication.md), [0033](adr/0033-command-pipeline.md), [0037](adr/0037-kiota-api-clients.md) | S2-a, S2-b, T1-a |

Kept here as the record: S2-c, S2-e, S2-f (superseded by O2-a), C1-f, P2-a, Q4-c, Q4-d, T1-b.

| # | Item | Decision | Why / alternative |
|---|---|---|---|
| S2-a | S2 | Integration events identify the message with `MessageId`, not `EventId` | "Event" means a ticketed event everywhere else; noted in [ADR-0007](adr/0007-module-communication.md) |
| S2-b | S2 | `INonTransactionalCommand` built in S2 (planned for O1): coordinators run without the transaction and audit steps | `StartCheckout` needed it for the first slice; noted in [ADR-0033](adr/0033-command-pipeline.md) |
| S2-c | S2 | Module APIs return plain outcome records (`BeginCheckoutOutcome`, ...), not `Result` | Contracts may reference only Shared.Contracts ([ADR-0002](adr/0002-module-boundary-rules.md)) |
| S2-d | S2 | A received webhook is stored and a scheduled message due at once hands it to Workers | Keeps WebhookIngress to "verify, store, 200" ([ADR-0009](adr/0009-checkout-without-shared-transaction.md)) |
| S2-e | S2 | Every host registers every module; each module decides per host kind what it adds | One composition list per host; the alternative, per-host module lists, drifts |
| S2-f | S2 | Paid-but-seats-lost orders end in status `SeatsLost`; the refund arrives with P2 | As planned |
| C1-a | C1 | Venues belong to an organizer and are never edited: a new layout is a new import | Events refer to venue seats; editing a layout under an event on sale would change it. Alternative: platform-wide venues managed by admins |
| C1-b | C1 | Seat-map import: seated sections list rows (label, seat count, first number); standing areas give a capacity. Grid positions and "best available" ranks are derived (front rows first, then seats nearest the middle) | Alternative: explicit coordinates per seat, as seat-map products do; much bigger files, nothing more to learn |
| C1-c | C1 | A venue seat's ID is that seat's identity in every event at the venue | Inventory's rows and the seat-map geometry join on it in the client ([ADR-0007](adr/0007-module-communication.md) rule 4) |
| C1-d | C1 | Price categories are assigned by rules: one per section, overridden per row; every place must be covered; resolved into `EventPublished` at publish time | Simple to state and validate; per-seat lists would be 2,000 lines of input |
| C1-e | C1 | The stored lifecycle is Draft → Published (Cancelled comes later). "On sale" is the time window [OnSaleAt, StartsAt), not a stored state; Inventory enforces it against the database clock (I1). A time in the past opens sales now; once sales start, the time can't move | A stored "OnSale" flipped by a scheduled job would lag behind the clock and give two answers |
| C1-f | C1 | `EventPublished` v1 changed in place (time zone, on-sale time, seat rank, standing areas) | Nothing has been released and the lab is disposable; contract versioning applies from the end of M1a |
| C1-g | C1 | `PricesChanged` carries the full price list; `OnSaleScheduled` is sent only for published events (a Draft's schedule travels in `EventPublished`) | Consumers apply a full list without knowing the previous one |
| C1-h | C1 | NodaTime's bundled TZDB validates IANA time zones | The repository runs with `InvariantGlobalization`, where `TimeZoneInfo` can't resolve IANA IDs on Windows and depends on tzdata in Linux images |
| C1-i | C1 | Commands on another organizer's event get 403 (from the authorizer); reads of it get 404; another organizer's venue in `CreateEvent` is "not found" | Reads don't confirm that someone else's IDs exist; commands fail closed in the pipeline |
| C1-j | C1 | `product/tests/Modules.IntegrationTests`: the real hosts in memory against one PostgreSQL, tests isolated by their own organizers and events; messages move only when a test steps the relay and scheduler | Deterministic, and it exercises the real migrations, pipeline and HTTP surface |
| C2-a | C2 | Search is PostgreSQL full-text search over a stored, generated `tsvector` of the event and venue names, using the `simple` configuration, a GIN index and `websearch_to_tsquery`; matches are ranked by `ts_rank`, then by date | `simple` doesn't stem: names mix languages and proper nouns. `websearch_to_tsquery` never fails on user input |
| C2-b | C2 | Events copy the venue's name and time zone when created | The venue never changes ([C1-a](#14-decided-during-m1a-implementation--proposed-please-review)), so the copy can't go stale, and browse needs no join |
| C2-c | C2 | The event list uses page numbers with `hasMore` and no total count; at most 50 per page | Counting every match on each request costs more than a total is worth. Keyset paging doesn't fit rank-ordered search |
| C2-d | C2 | The seat map is served at `/events/{id}/seat-map/{version}`, where the version is a 12-hex-digit SHA-256 of its content, set at publish. It carries the layout and each seat's category but no prices; unknown versions get 404 | Price changes don't invalidate the cached map. The same layout always gets the same URL ([ADR-0028](adr/0028-gateway-and-browse-caching.md)) |
| C2-e | C2 | `Cache-Control`: browse responses `public, max-age=30`; the seat map `public, max-age=31536000, immutable` | Short lifetimes instead of purges (ADR-0028); output caching in Valkey comes with M3 |
| C2-f | C2 | The public list shows published events that haven't started, including ones not on sale yet, each with its sales phase | Fans see upcoming on-sales; drafts are never public (404) |
| G1-a | G1 | The Organizers module keeps organizers and a member list, but in M1a the token stays the authority on membership (Keycloak groups). Adding a member here doesn't grant access yet | Keeping Keycloak in step is M1b ([M1a scope](milestones/m1a.md)); until then this list is the record an admin keeps next to the realm |
| G1-b | G1 | Admins register organizers, optionally under an existing Keycloak group's ID, which is how the seeded organizer is registered (by the seeder and `BackOfficeApi.http`) | Seeding product tables from a migration would put development data into the product's schema history |
| G1-c | G1 | An organizer's members and admins manage its member list; other callers get 404 on reads and 403 on changes | Same rule as C1-i |
| I1-a | I1 | The sales window is checked inside the claim statement (`EXISTS` on `inventory.sales_windows` against `now()`); only when a claim fails does a second query ask whether the event is on sale, to choose between `hold.not_on_sale` and `hold.seats_unavailable` | A successful hold stays one claim statement ([ADR-0011](adr/0011-seat-inventory-concurrency.md)) |
| I1-b | I1 | Inventory's sales window remembers when Catalog set the on-sale time and ignores older values | Retried deliveries let messages from one outbox overtake each other: `OnSaleScheduled` may arrive before `EventPublished` |
| I1-c | I1 | Standing slots get random IDs when created; their rank is the slot number | Slots are interchangeable; best available on a standing area takes the lowest free numbers |
| I1-d | I1 | One current hold per fan per event: the handler checks first, and a partial unique index on (event, fan) WHERE status IN (Active, InCheckout) catches the race. An Active hold whose time ran out is marked Expired lazily, when the fan places their next hold. Modules name such constraints; the transaction runner turns a violation into the named error (shared change) | Check for the clear message, index for correctness under concurrency |
| I1-e | I1 | Fans release their own Active hold (`DELETE /holds/{id}`); a hold in checkout can be released only through its order (I2). Someone else's hold is "not found" | With one hold per fan, changing seats would otherwise mean waiting for expiry |
| I1-f | I1 | `GET /events/{id}/availability` replaces S2's `/events/{id}/seats`: free and total places per section, plus the free seat IDs (standing slots are counted, not listed); database clock; advisory | The seat map has the seat details now (C2); this stays small and changes fast |
| I1-g | I1 | A hold's `ExpiresAt` is the `held_until` its claim wrote (database clock), not application time | Hold and seat rows agree exactly |
| I1-h | I1 | `ModuleSql` (shared) is public, so modules can run hand-written queries that return rows inside their transaction | The claim needs `UPDATE … RETURNING` |
| I2-a | I2 | **Recurring scheduled messages** in the shared scheduler: each has exactly one pending occurrence. The scheduler inserts the first one under an advisory lock if none is pending (checked about once a minute, which also restarts a dead-lettered job), and schedules the next one in the same transaction as each run. The sweeper is the first; reconciliation (M4) can reuse it | ADR-0010 puts the sweeper on the scheduler; any number of Workers instances, no leader election |
| I2-b | I2 | The sweeper only shows expired seats as Available, and keeps their hold ID, so a late confirmation still sells seats nobody took (the same rule as lazy expiry). It marks lapsed holds Expired, including holds in checkout past the backstop. Batches of 1,000, `SKIP LOCKED` | "Only tidies up" (ADR-0010): sweeping never changes who can buy a seat |
| I2-c | I2 | `IInventoryApi.BeginCheckoutAsync` takes the fan explicitly (as drawn in ADR-0009) instead of reading the HTTP caller | A module API designed as if remote mustn't depend on the caller's HTTP context |
| I2-d | I2 | At checkout, the hold and its seats get the same backstop expiry, from the database clock (`UPDATE … RETURNING`) | Same reason as I1-g |
| I2-e | I2 | `ReleaseHoldAsync` returns Released (also when repeated, expired or lost) or AlreadyConfirmed; a hold that doesn't belong to the order throws, like `ConfirmHoldAsync`, so the message retries and dead-letters | Safe to repeat; a mismatch is a bug, not an outcome |
| L1-a | L1 | LocalPay scenarios are chosen by the last two digits of the amount in minor units: .01 approving declines, .02 webhook late (10 s by default), .03 webhook twice, .04 no webhook, .05 expiring races with the fan's payment, which wins. The checkout page names the scenario | Like Stripe's test cards (docs/payments.md); tests trigger them through prices |
| L1-b | L1 | Webhooks are delivered in the background and retried until the shop answers 2xx (5 attempts, doubling from 1 s); a retry or duplicate repeats the event ID | Real providers retry; the shop must drop duplicates by event ID |
| L1-c | L1 | `POST /v1/payments/{id}/expire`: 200 when expired (also when repeated), 409 with the payment when it already succeeded or failed | Mirrors Stripe refusing to expire a completed Checkout Session; the adapter maps 409 + succeeded to AlreadyCompleted |
| L1-d | L1 | Refunds succeed at once (no pending state), may be partial, take an Idempotency-Key and send `refund.succeeded` | Enough for P2's single refund; asynchronous refunds can come with M4's mass refunds |
| L1-e | L1 | Left for M1b/M4 as planned: timeouts and 500s when creating a payment, rate limits, sampled latencies, payment listing, out-of-order webhooks | M1a scope table |
| L1-f | L1 | `tooling-ci` now runs the tooling tests and checks that tooling's OpenAPI documents are committed and current (same job, so the required check is unchanged) | The adapter is written against LocalPay's published document |
| P1-a | P1 | `IPaymentProvider.CancelPaymentAsync` returns Cancelled whenever nothing can be paid any more (expired, failed, unknown to the provider) and AlreadyCompleted only for a success; `ListPaymentsAsync` exists but LocalPay doesn't support it until reconciliation (M4) | Orders needs one decision: release the seats, or wait for the webhook |
| P1-b | P1 | `IPaymentsApi` adds `CancelCheckoutAsync` (Cancelled / AlreadyCompleted / ProviderUnavailable) and `CheckPaymentStatusAsync`; a success found by a status check publishes `PaymentSucceeded` exactly like a webhook (shared `PaymentOutcomes`) | O1's deadline flow and "ask the provider if no webhook within 2 minutes" need both |
| P1-c | P1 | Webhooks are matched to the attempt by the provider's payment ID, or by our reference (the order ID) when the webhook overtook the call that stores the session; storing the session never undoes an outcome that arrived first | Out-of-order webhooks (the Done-when of P1) |
| P1-d | P1 | A webhook whose amount or currency differs from the attempt marks nothing paid: its processing throws, retries and dead-letters for a person to look at | "Amount checked" (docs/payments.md) |
| P1-e | P1 | A duplicate webhook is acknowledged with 200, also when two arrive at the same moment (the unique index maps to `webhook.duplicate`) | The provider stops retrying; the inbox drops it |
| P1-f | P1 | **Shared:** the transaction runner clears tracked entities after each commit, and turns `DbUpdateConcurrencyException` into `ConcurrencyConflictException` (safe to retry). `PaymentAttempt` has an `xmin` concurrency token; `CreateCheckout` retries storing the session once on a conflict | A test caught a lost update: a coordinator's stale copy of the attempt overwrote the webhook's success. Holds and orders get the same token with O1 |
| P1-g | P1 | Module tests fake LocalPay at the HTTP level from its published API (all outgoing HTTP goes to the fake); WebhookIngress joins the in-memory hosts | Product tests can't use the simulator (ADR-0021), and the real adapter still runs |
| O1-a | O1 | Order states: Pending → Confirmed, Expired (deadline), Failed (declined) or SeatsLost (refund in P2); only Pending moves, so every transition is safe to repeat. Expired and Failed checkouts publish no event | Abandoned checkouts are nobody's business outside Orders; `OrderCancelled` is for sold orders (M4) |
| O1-b | O1 | **Shared:** scheduled handlers marked `INonTransactionalMessageHandler` coordinate: they run outside any transaction, each step is a command or module call with its own, and the row is deleted afterwards (a crash in between repeats the handler) | ADR-0033's rule for coordinators: no transaction open across a provider call |
| O1-c | O1 | The deadline (10 min) is scheduled with the order, in its transaction. At the deadline: cancel the payment; Cancelled → release the hold → order Expired; AlreadyCompleted → ask the provider after 2 min if no webhook confirmed it; provider unreachable → try again after 30 s | ADR-0010, docs/payments.md |
| O1-d | O1 | A declined payment closes the checkout at once through the same steps (payment cancelled, seats released, order Failed) instead of waiting for the deadline | The fan can start again immediately; same safe order of steps |
| O1-e | O1 | The price list keeps, per category, the price Catalog set last (`set_at` from the message), so `PricesChanged` and `EventPublished` can arrive in either order | Retried deliveries reorder messages (as I1-b) |
| O1-f | O1 | Orders and holds have `xmin` concurrency tokens, like payment attempts (P1-f) | Payment events and the deadline can touch the same order or hold at once; the later save fails and its message retries |
| P2-a | P2 | P2 (the Payments refund) is built before O2, so O2's seats-lost path can end Refunded, which is P2's Done-when | O2's "seats lost → refund" needs the refund to exist |
| P2-b | P2 | One full refund per payment in M1a (unique per payment attempt), with our refund ID as the provider's idempotency key; a repeated request reuses the refund and asks the provider again only while it is still Requested | "Single refund" (M1a scope); safe to retry after a timeout |
| P2-c | P2 | A refund's outcome comes from the provider's API answer or its `refund.succeeded` webhook, whichever is first; `RefundSucceeded` / `RefundFailed` are published once | LocalPay answers at once; providers with asynchronous refunds report by webhook |
| P2-d | P2 | A refund LocalPay refuses (409) ends as `RefundFailed` with the provider's reason; a person follows up | No automatic retry for a refusal; retries are for unreachability only |
| O2-a | O2 | A paid order that can't get its seats goes Refunding → Refunded (or RefundFailed for a person). Two reasons: SeatsLost, and PaidAfterClose (money for a checkout that had already closed, the race ADR-0010 makes rare); both use the same path | One refund path for every "paid but no seats" case |
| O2-b | O2 | The refund is asked for by a coordinating scheduled message (`RefundOrder`), scheduled in the same transaction that starts the refund; an unreachable provider is retried after 30 s | No provider call inside the consumer's transaction (as O1-b) |
| O2-c | O2 | Orders publishes `OrderRefunded` (event catalogue, ADR-0007) when the refund succeeds; nothing consumes it yet (Notifications, M1b) | Cheap now; the consumer arrives later without touching Orders |
| O2-d | O2 | `GET /orders/{id}` (the return page's poll) returns status, deadline, seats and refund reason with `Cache-Control: no-store`; another fan's order is 404 | The redirect proves nothing; the page polls until the status settles |
| T1-a | T1 | Kiota CLI 1.35.0 (local tool) and `Microsoft.Kiota.Bundle` 2.1.2: the newest releases at least two weeks old. Clients for BackOfficeApi and PublicApi in `tooling/clients`, regenerated by `tooling/clients/generate.sh` (run from anywhere; paths are recorded relative to the repository); `tooling-ci` regenerates and fails on any difference | ADR-0037 |
| T1-b | T1 | The seeder (`tooling/seed`) prepares a fresh "Demo Concert" at a new "Demo Arena" (2,000 seats in four tribunes, a 500-place standing floor, three price categories) on every run, registers the realm's organizer once, opens sales now and waits until Inventory has the places | The lab is disposable; a fresh event per run keeps demos independent |
| T1-c | T1 | The seeder is also the AppHost resource "seed" with explicit start (press Start in the dashboard), and a system test starts it and finds the event on sale | "One command prepares the demo", checked in CI |
| Q3-a | Q3 | A purchase is one trace from `POST /orders` to the confirmation: Payments stores the checkout's trace context with the payment attempt and publishes `PaymentSucceeded` from a span in that trace; the hold's request and the webhook's request are linked, not parented | Separate requests keep their own traces; links connect them without pretending one caused the other |
| Q3-b | Q3 | Hold results and checkout outcomes come from the command metric, which now carries the error code (a small, fixed set per command); business meters per module add order transitions, payment outcomes, webhooks, refunds, places held and sweeps. Catalogue in `docs/observability.md` | One generic metric covers every command's outcomes; module meters cover what isn't a command |
| Q3-c | Q3 | Tags take values only from small, fixed sets: never IDs, user names or amounts | Cardinality |
| Q1-a | Q1 | The T2 test runs in `Modules.IntegrationTests` on every CI build: 500 fans at once on 100 seats (half pick 2–4 adjacent seats, half ask for the best seat), a top-up until sold out, then every hold checked out and paid, every tenth after its backstop passed and a seat was taken. Invariants: no seat in two holds, rows match holds, all 100 held, every order Confirmed with exactly its seats or Refunded once with none, seat states add up to capacity | ADR-0015 T2 ("CI integration tests with parallel holds"); a mutation check showed it catches a claim that overwrites held seats |
| Q2-a | Q2 | `POST /orders` for a hold that already has a Pending order resumes it (same order, same payment page; Payments' idempotency key finds the provider's session), also without an Idempotency-Key and when two requests race. Another fan gets 409; a closed order gets `checkout.order_closed` | Recovers the crash rows "after the order is created" and "provider call before the session is stored" from the fan's side: retrying works |
| Q2-b | Q2 | Crash points are tested by stopping the flow where the crash would (calling the module API step directly, or making LocalPay fail or lose its answer for one price), then letting recovery run | Deterministic, no process kills needed |
| Q2-c | Q2 | Module tests run the sweeper only when a test makes it due | A sweep's momentary row locks make concurrent claims skip just-expired seats (ADR-0011's documented trade-off), which made tests flaky |
| Q4-a | Q4 | Demo mode is a switch on the AppHost (`dotnet run --project dev/AppHost -- --Demo=true`): holds last 1 minute, checkouts 2. The system tests run the AppHost in demo mode | The demo script's step 7 needs expiry within minutes; tests and demo then share one configuration |
| Q4-b | Q4 | The dry run is automated: `DemoDryRunTests`, `DemoHoldExpiryTests` and `DemoCheckoutDeadlineTests` walk the demo script against the real AppHost on every PR (the timed classes run in parallel, about 3 minutes). The `.http` path was also replayed by hand on `dotnet run` | "A dry run passes" stays true, not a one-off. The dry run found two bugs the module tests couldn't (below) |
| Q4-c | Q4 | **Fixed:** Workers had no reference to LocalPay, so deadline cancellations, status checks and refunds failed in the running app (module tests fake LocalPay for every host). Keycloak was served over HTTPS on `dotnet run` (not in tests), breaking `http://localhost:8180` in the `.http` files and the seeder; it now stays plain HTTP (`WithoutHttpsCertificate`, an experimental Aspire API, warning ASPIRECERTIFICATES001 suppressed at that call) | Found by the dry run |
| Q4-d | Q4 | The realm gets four more fans (cid, dan, eva, fil); the seeder also creates "Failure Scenarios", priced so one seat's amount picks each LocalPay scenario. Runbook: `docs/demos/m1a.md` | One hold per fan per event needs more fans for parallel demo steps; scenarios are triggered by prices (L1-a) |

## 15. M1b planning — Agreed 2026-10-20

Decisions D7–D19 (D1–D6 were the fan site and keeping the Scaling Lab in M2, 2026-10-19). The plan:
[M1b](milestones/m1b.md).

| # | Decision | Recorded in |
|---|---|---|
| D7 | Build the Stripe adapter without an account: `stripe-mock` in CI, signature tests with our own signed payloads; Stripe off unless configured | [0050](adr/0050-stripe-adapter-without-an-account.md) |
| D8 | A setting picks the provider; the fan chooses only when more than one is enabled | [0050](adr/0050-stripe-adapter-without-an-account.md) |
| D9 | QR codes are JWS signed with ES256 (ticket, event, version, key ID) | [0051](adr/0051-signed-tickets-and-their-keys.md) |
| D10 | A key pair per event; private keys stored under envelope encryption with a master key from secrets | [0051](adr/0051-signed-tickets-and-their-keys.md) |
| D11 | No PDF tickets until M4; QR codes in "My tickets" and in the email | [0051](adr/0051-signed-tickets-and-their-keys.md) |
| D12 | Email through MailKit to Mailpit, Razor templates, a delivery log | [0052](adr/0052-email-through-mailpit.md) |
| D13 | Buyer email and event name added to version 1 of the order events | [0007](adr/0007-module-communication.md) (update) |
| D14 | Organizers keeping Keycloak in step stays in M1b, cut first | [M1b](milestones/m1b.md) |
| D15 | LocalPay in M1b: create timeouts and 500s, webhooks out of order; the rest in M4 | [M1b](milestones/m1b.md) |
| D16 | The per-fan ticket limit is enforced by Inventory at hold time | [0041](adr/0041-holds-sales-window-release-and-sweeper.md) (update) |
| D17 | Not in M1b: the back-office UI, ticket revocation, CheckIn, the Scaling Lab | [M1b](milestones/m1b.md) |
| D18 | Team of five; M1b time-boxed to three weeks; M1a closed as done | [0016](adr/0016-teaching-milestones.md) (update) |
| D19 | The demo runs on one laptop, offline, with one presenter | [0016](adr/0016-teaching-milestones.md) (update), [M1b](milestones/m1b.md) |

## 16. After M1b — Agreed 2026-10-20

| # | Decision | Recorded in |
|---|---|---|
| D20 | The Scaling Lab's basics (load generator, slider, live tiles and charts, first presets) are built now, under Aspire, instead of in M2; Kubernetes, pods and autoscaling stay in M2–M5. Revises D6 | [0017](adr/0017-scaling-lab-and-load-generator.md) (update), [0016](adr/0016-teaching-milestones.md) (update) |

## 17. M2 planning — Agreed 2026-10-21

The owner approved the recommended answers ("go with the plan … with the recommended answers") and
asked for the work up to several instances first. The plan: [M2](milestones/m2.md), where each
decision's reasoning and alternatives are.

| # | Decision | Recorded in |
|---|---|---|
| D21 | M2 as planned, built up to "several instances, visible in the lab" first | [M2](milestones/m2.md) |
| D22 | The lab cluster is made by the `kind` tool, downloaded by the lab script, not Docker Desktop's built-in Kubernetes | [0020](adr/0020-local-kubernetes-on-docker-desktop.md) (update) |
| D23 | One node in M2; three zone-labelled nodes in M3 | [0020](adr/0020-local-kubernetes-on-docker-desktop.md) (update) |
| D24 | The lab script is a .NET 10 file-based app, `lab.cs`, instead of PowerShell 7 | [0022](adr/0022-disposable-lab-environment.md) (update) |
| D25 | Images reach the cluster with `kind load` (verified), on `noble-chiseled`, tagged in the lab with a hash of their sources | [0029](adr/0029-container-image-per-host.md) (update) |
| D26 | Helm: the product chart with lab values, a tooling chart, the platform from pinned charts | [M2](milestones/m2.md), [0021](adr/0021-product-tooling-platform-separation.md) |
| D27 | Platform in M2: Traefik with Gateway API, CloudNativePG (one instance), Keycloak, Mailpit, metrics-server, the Aspire Dashboard | [0053](adr/0053-the-lab-cluster-addresses-identity-and-platform.md) |
| D28 | The same localhost ports as under Aspire, through the cluster's port mappings | [0053](adr/0053-the-lab-cluster-addresses-identity-and-platform.md) |
| D29 | One token issuer, `http://localhost:8180`; services reach Keycloak internally | [0053](adr/0053-the-lab-cluster-addresses-identity-and-platform.md) |
| D30 | Instances in M2: PublicApi 3, WebhookIngress 2, Workers 2, the rest 1 | [M2](milestones/m2.md) |
| D31 | The Scaling Lab shows instances and changes PublicApi's count | [0017](adr/0017-scaling-lab-and-load-generator.md) (update) |
| D32 | The Migrator runs as a Helm hook Job before every install and upgrade | [0008](adr/0008-hosts.md) (update) |
| D33 | Secrets generated by every `lab up`; Stripe off in the lab | [0025](adr/0025-object-storage-and-secrets.md) (update) |
| D34 | product-ci builds every image; a `lab` workflow runs `lab up` in kind on deployment changes and nightly | [0036](adr/0036-trunk-based-development.md) (update) |
| D35 | M2 time-boxed to three weeks, team of five | [0016](adr/0016-teaching-milestones.md) (update) |

## 18. After the first M2 cut — Agreed 2026-10-21

Proposed while the owner used the lab; the owner agreed to the recommendations ("OK, proceed …
no need to ask me for approval") and asked for the rest of M2 without review stops. Reasoning
and alternatives: [M2](milestones/m2.md#decisions) and [0054](adr/0054-publicapi-autoscaling-switch-in-the-lab.md).

| # | Decision | Recorded in |
|---|---|---|
| D36 | Service-to-service HTTP clients recycle their pooled connections every minute, so new instances get traffic | [0019](adr/0019-autoscaling-signals.md) (update) |
| D37 | PublicApi's autoscaling switch is brought forward from M5 | [0054](adr/0054-publicapi-autoscaling-switch-in-the-lab.md), [0016](adr/0016-teaching-milestones.md) (update) |
| D38 | KEDA in the lab platform; a `ScaledObject` for PublicApi in the product chart; deploys keep the live count | [0054](adr/0054-publicapi-autoscaling-switch-in-the-lab.md) |
| D39 | Auto off is KEDA's `paused` annotation; − / + work as before; the state lives in the cluster | [0054](adr/0054-publicapi-autoscaling-switch-in-the-lab.md), [0017](adr/0017-scaling-lab-and-load-generator.md) (update) |
| D40 | CPU as the signal until Prometheus (M3), with PublicApi's CPU request calibrated to 20 requests/s per instance | [0054](adr/0054-publicapi-autoscaling-switch-in-the-lab.md), [0019](adr/0019-autoscaling-signals.md) (update) |
| D41 | 1 to 8 instances; demo-speed and realistic profiles switched in the lab | [0054](adr/0054-publicapi-autoscaling-switch-in-the-lab.md) |
| D42 | Two hundred lab fans instead of twenty | [0017](adr/0017-scaling-lab-and-load-generator.md) (update) |
| D43 | New instances don't count as load: a warm-up before readiness, no dynamic PGO in the lab, a 20-second scale-up window (decided while building, for review) | [0054](adr/0054-publicapi-autoscaling-switch-in-the-lab.md) (update), [lab](lab.md#autoscaling) |
