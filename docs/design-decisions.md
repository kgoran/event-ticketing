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
| API clients (tooling) | Kiota, generated from the committed OpenAPI documents | Agreed — [ADR-0037](adr/0037-kiota-api-clients.md) |
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
| M2 | Locally built images reach Docker Desktop's kind cluster ([ADR-0029](adr/0029-container-image-per-host.md)) | Local registry container started by `lab up` |
| M3 | CloudNativePG backups and restores against SeaweedFS ([ADR-0025](adr/0025-object-storage-and-secrets.md)) | RustFS |
| M3 | Scanner device credentials as Keycloak clients ([ADR-0027](adr/0027-identity-provider-keycloak.md)) | Device tokens issued by CheckIn |
| M5 | KEDA scaling CloudNativePG read replicas ([ADR-0019](adr/0019-autoscaling-signals.md)) | Small lab controller that changes the instance count |

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

## 14. Decided during M1a implementation — Proposed (please review)

Implementation-level choices made while building M1a, where neither an ADR nor the plan
said. Each stays Proposed until reviewed; agreed items with lasting weight become ADRs (or
updates to existing ones), and the rest stay here as the record.

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
