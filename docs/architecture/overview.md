# Architecture overview

**Status:** Agreed on 2026-10-18. This document is the overall picture. The reasons behind each
decision, and the alternatives we rejected, are in the ADRs linked from each section
([index](../adr/README.md)). The one remaining question (where the repository lives) and the verification spikes are in
[`design-decisions.md` §13](../design-decisions.md#13-open-questions).

Contents: [1 Context](#1-system-context) · [2 Modules](#2-modules) · [3 Hosts](#3-hosts) ·
[4 Key flows](#4-key-flows) · [5 Seat inventory](#5-seat-inventory-and-hot-events) ·
[6 Waiting room](#6-waiting-room) · [7 Performance targets](#7-performance-targets) ·
[8 Milestones](#8-milestones) · [9 Repository and environments](#9-repository-and-environments) ·
[10 Scaling Lab](#10-scaling-lab)

---

## 1. System context

```
   fans              door staff          organizers, admins
     │                    │                        │
     ▼                    ▼                        ▼
┌───────────────────────────────────────────────────────────┐
│                      EventTicketing                       │
│   10 modules · 7 hosts · PostgreSQL · Valkey · Kafka      │
└──────┬─────────────────────┬──────────────────────┬───────┘
       ▼                     ▼                      ▼
 Stripe test mode      email: Mailpit        identity provider
 or LocalPay           (locally)             (Keycloak)
```

- A modular monolith: one codebase with strict module boundaries, deployed as several
  stateless hosts ([0001](../adr/0001-modular-monolith-with-several-hosts.md),
  [0002](../adr/0002-module-boundary-rules.md)).
- Core platform: PostgreSQL (CloudNativePG), Kafka protocol (Redpanda), Valkey, Kubernetes with
  KEDA ([0003](../adr/0003-core-platform-technologies.md)).
- No paid services; everything runs locally, the lab on Kubernetes inside Docker Desktop
  ([0005](../adr/0005-no-paid-services-local-first.md),
  [0020](../adr/0020-local-kubernetes-on-docker-desktop.md)).
- Single-tenant; organizers own their data ([0023](../adr/0023-single-tenant-organizer-scoped-data.md)).
  What's in and out of scope: [0024](../adr/0024-domain-scope-m1-m6.md).
- Identity: Keycloak; the product speaks only standard OIDC
  ([0027](../adr/0027-identity-provider-keycloak.md)).
- Code: .NET 10 LTS, ASP.NET Core minimal APIs, EF Core with Npgsql, our own command pipeline,
  Blazor for the UIs ([0030](../adr/0030-runtime-and-api-style.md),
  [0031](../adr/0031-data-access-ef-core.md), [0033](../adr/0033-command-pipeline.md),
  [0035](../adr/0035-blazor-for-all-uis.md)); patterns inside modules in
  [0032](../adr/0032-patterns-inside-modules.md), tests in [0034](../adr/0034-test-stack.md).

---

## 2. Modules

Ten modules, each with its own schema, DbContext and database login
([0006](../adr/0006-ticketing-modules.md)).

| Module | Owns | Synchronous API (called by) | Publishes | Consumes |
|---|---|---|---|---|
| **Catalog** | venues, seat-map layouts, events, price categories, on-sale schedule, event lifecycle | — | EventPublished, PricesChanged, OnSaleScheduled, EventCancelled | — |
| **Inventory** | one row per sellable place per event (seat or standing slot), holds, availability snapshots | BeginCheckout, ConfirmHold, ReleaseHold (Orders) | SoldOut | EventPublished (creates the seat rows), OnSaleScheduled, EventCancelled |
| **Orders** | orders, prices recorded on each order, checkout coordinator, event-cancellation progress | — | OrderConfirmed, OrderCancelled, OrderRefunded, OrderRevoked | payment and refund events, DisputeOpened, EventCancelled, EventPublished and PricesChanged (local price list) |
| **Payments** | payment attempts, provider adapters, incoming webhooks, refunds, reconciliation, disputes | CreateCheckout, CancelCheckout, RequestRefund (Orders) | PaymentSucceeded/Failed, RefundSucceeded/Failed, DisputeOpened | nothing: depends on no module |
| **Ticketing** | tickets, signed QR codes, signing keys, PDFs, revocations | — | TicketsIssued, TicketsRevoked | OrderConfirmed, OrderRefunded, OrderRevoked, EventCancelled |
| **CheckIn** | door lists (manifests), scanner devices, scan log, duplicate entries | — | TicketCheckedIn, DuplicateEntryDetected | TicketsIssued, TicketsRevoked, EventCancelled |
| **Notifications** | email templates, delivery log, email adapter | — | — | TicketsIssued, OrderCancelled, OrderRefunded |
| **Organizers** | organizers, members, roles | none: membership travels in the login token | OrganizerCreated | — |
| **Reporting** | sales and check-in projections | — | — | most events |
| **WaitingRoom** | queues, positions, admission rate, admission-token keys (Valkey) | none: PublicApi verifies its tokens locally | — | OnSaleScheduled, SoldOut, EventCancelled |

**How modules talk** ([0007](../adr/0007-module-communication.md)):

- Synchronous calls only where the caller needs a decision from the owning module to continue:
  Orders → Inventory and Orders → Payments. Nothing calls back into Orders, so there are no
  call chains or cycles.
- Everything else goes through integration events (outbox → transport → inbox,
  [0014](../adr/0014-outbox-inbox-scheduler-on-postgresql.md)). Events carry the data their
  consumers need.
- Pages that need two modules' data are assembled by the client, not by modules calling each
  other.
- No database transaction ever spans two modules.

```
Legend  ══►  synchronous call (in-process; gRPC if a module is ever extracted)
        ──►  integration event (outbox → broker → inbox)

┌─────────┐  EventPublished · PricesChanged · OnSaleScheduled · EventCancelled
│ Catalog │──────► Inventory, Orders, WaitingRoom, Ticketing, CheckIn, Reporting
└─────────┘

┌───────────┐  BeginCheckout   ┌──────────┐  CreateCheckout   ┌──────────┐
│ Inventory │◄═════════════════│  Orders  │══════════════════►│ Payments │◄── webhooks
│           │  ConfirmHold     │ checkout │  CancelCheckout   │          │    Stripe,
│           │  ReleaseHold     │ process  │  RequestRefund    │          │    LocalPay
└─────┬─────┘                  │ manager  │◄──────────────────│          │
      │ SoldOut                └────┬─────┘  PaymentSucceeded └──────────┘
      ▼                             │        PaymentFailed
 WaitingRoom                        │        RefundSucceeded/Failed
                                    │        DisputeOpened
                                    │ OrderConfirmed · OrderCancelled
                                    │ OrderRefunded  · OrderRevoked
          ┌─────────────────────────┼─────────────────────────┐
          ▼                         ▼                         ▼
    ┌───────────┐           ┌───────────────┐           ┌───────────┐
    │ Ticketing │           │ Notifications │           │ Reporting │◄── almost every event
    └─────┬─────┘           └───────▲───────┘           └─────▲─────┘
          ├── TicketsIssued ────────┘                         │
          │ TicketsIssued · TicketsRevoked                    │
          ▼                                                   │
    ┌───────────┐  TicketCheckedIn                            │
    │  CheckIn  │─────────────────────────────────────────────┘
    └───────────┘
```

---

## 3. Hosts

Seven deployables ([0008](../adr/0008-hosts.md)); what each one scales on is in
[0019](../adr/0019-autoscaling-signals.md).

| Host | Runs | Clients |
|---|---|---|
| **PublicApi** | Catalog (read), Inventory (availability, holds), Orders (checkout, status), Ticketing (my tickets) | fans |
| **WaitingRoom** | WaitingRoom (Valkey only) | fans during an on-sale |
| **WebhookIngress** | Payments: verify, store in inbox, reply 200 | Stripe (forwarded by the Stripe CLI locally), LocalPay |
| **BackOfficeApi** | Catalog (manage), Organizers, Orders/Payments admin, Reporting, CheckIn device registration | organizers, admins |
| **CheckInApi** | CheckIn: door-list download, scan upload | scanner devices |
| **Workers** | all consumers, outbox relay, scheduler, PDF rendering, email | — |
| **Migrator** | migrations for all modules, as a Kubernetes Job per release | pipeline |

Lab environment (stateless hosts above the `═` line, state below):

```
             fans                   door staff   organizers/admins  Stripe/LocalPay
       ┌───────┴────────┐                │                │                │
  ┌────▼────────────────▼────────────────▼────────────────▼─────┐          │
  │ gateway: Traefik (Gateway API) · TLS, routing, rate limits  │          │
  └────┬────────────────┬────────────────┬────────────────┬─────┘          │
┌──────▼───────┐ ┌──────▼───────┐ ┌──────▼───────┐ ┌──────▼───────┐ ┌──────▼───────┐
│ WaitingRoom  │ │  PublicApi   │ │  CheckInApi  │ │ BackOfficeApi│ │WebhookIngress│
└──────┬───────┘ └──────┬───────┘ └──────┬───────┘ └──────┬───────┘ └──────┬───────┘
═══════╪════════════════╪════════════════╪════════════════╪════════════════╪════════
┌──────▼───────┐ ┌──────▼────────────────▼────────────────▼────────────────▼───────┐
│    Valkey    │ │ PostgreSQL: primary ── sync standby ── read replicas            │
│ queue, cache │ │ one schema per module · outbox, inbox, scheduled messages       │
└──────────────┘ └──────▲──────────────────────────────────────────────────────────┘
                        │
                 ┌──────┴──┐   ┌───────┐
                 │ Workers │◄─►│ Kafka │  from M4 (M1–M3: relay dispatches in-process)
                 └────┬────┘   └───────┘
                      └──► payment provider API · email · object storage (PDFs)
```

PublicApi also uses Valkey (browse output cache, availability snapshot, sold-out flags) and calls
the payment provider to create checkout sessions
([0028](../adr/0028-gateway-and-browse-caching.md)). Each host ships as its own container image
([0029](../adr/0029-container-image-per-host.md)). The cluster has three nodes labelled as zones; the
synchronous standby runs on a different node from the primary. Migrator runs once per release.

---

## 4. Key flows

### 4.1 Browse events

```
fan ─► gateway ─► PublicApi ─► output cache in Valkey ─hit─► event list, event page
                                                     └miss─► Catalog query ─► read replica
fan ─► gateway ─► PublicApi ─► seat-map geometry (never changes; versioned URL, cached for good)
fan ─► gateway ─► PublicApi ─► Inventory availability snapshot in Valkey (cached 1–2 s)
                               ▲ rebuilt about once a second per on-sale event by one job
                                 (a single query on the primary)
```

- The seat-map layout never changes, so it is cached forever. Availability is a small bitmap
  (about 10 KB for 80,000 seats) with a short cache lifetime.
- Read replicas and caches only serve browsing; they never decide anything.
- Search uses PostgreSQL full-text search. Cache invalidation uses versioned keys and short
  lifetimes instead of purges.
- Browse responses are cached by ASP.NET Core output caching inside PublicApi, shared through
  Valkey ([0028](../adr/0028-gateway-and-browse-caching.md)).
- No CDN locally; browse responses carry `Cache-Control` headers so one could be added later
  ([0005](../adr/0005-no-paid-services-local-first.md)).

### 4.2 Hold seats

```
fan ─► WaitingRoom (for protected on-sales) ─► signed admission token {eventId, fanId, expires ≈ 15 min}
fan ─► PublicApi POST /events/{id}/holds  {seatIds | zone + qty}   Idempotency-Key, admission token
         check the token locally · per-fan rate limit · sold-out flag in Valkey → instant 409
         Inventory.PlaceHold: ONE SQL statement, ONE round trip, on the primary:
           claim the seats + insert the hold — all or nothing
       ◄─ 201 {holdId, expiresAt}  |  409 {unavailable}
```

At most one active hold per fan per event; a maximum number of seats per hold; hold length
about 8 minutes (a policy). Details: [0011](../adr/0011-seat-inventory-concurrency.md).

### 4.3 Checkout and payment

Each module commits its own transaction; Orders coordinates
([0009](../adr/0009-checkout-without-shared-transaction.md), [payments.md](../payments.md)).

```
fan ─► PublicApi POST /orders {holdId}        Idempotency-Key; one order per hold
  Orders.StartCheckout — no transaction spans these steps
  1 ══► Inventory.BeginCheckout(holdId, orderId, fanId)
          hold → InCheckout, expiry pushed out to a backstop (~35 min)
          ◄ seats + price categories  |  HoldNotActive → 409
  2 tx(orders): order Pending, prices from the local price list,
          deadline = now + 10 min, schedule CheckoutDeadline(orderId)
  3 ══► Payments.CreateCheckout(orderId, amount, providerExpiry = now + 30 min)
          tx: attempt Created → provider call (idempotency key = orderId) → tx: store session
  ◄─ {orderId, redirectUrl}

fan pays on the provider's page ─► redirected to /orders/{id}, which polls (the redirect proves nothing)
provider ─► WebhookIngress: check signature → store in payments inbox (unique event id) → 200
Workers · Payments: attempt Succeeded (amount checked) → outbox PaymentSucceeded
Workers · Orders:   ══► Inventory.ConfirmHold(holdId, orderId)          safe to repeat
                      Confirmed → order Confirmed → outbox OrderConfirmed
                      SeatsLost → order Refunding ══► Payments.RequestRefund
                                  → outbox OrderCancelled(reason)
```

### 4.4 Ticket issuance

```
OrderConfirmed ─► Ticketing (Workers):
   render PDFs + upload to object storage (safe to repeat, keyed by ticketId)  ← side effects first
   tx: one ticket per seat (unique per order + seat), QR = signature over (ticketId, eventId, version)
       + outbox TicketsIssued {ticketIds, document keys}
TicketsIssued ─► Notifications (email) · CheckIn (door list) · Reporting
"My tickets" in PublicApi reads from Ticketing directly, so fans have their tickets even if the email fails.
```

### 4.5 Check-in at the door

Works offline first ([0013](../adr/0013-offline-first-check-in.md)).

```
before doors   scanner ─► CheckInApi GET door list (eventId, since=cursor)
               ◄ ticket ids + revocations + event public key   (~2–4 MB for 80k tickets, no personal data)
at the door    scan → check signature → on the list? not revoked? not already scanned on this device?
               → green/red in < 300 ms, no network needed
when online    every few seconds: POST a batch of scans (safe to repeat, keyed by scanId);
               GET changes since the cursor (new tickets, revocations, scans from other devices)
server         the first accepted scan wins; a second accepted scan → DuplicateEntryDetected (alert)
```

Inventory refuses a sale rather than risk selling a seat twice; the door lets someone in rather
than stop the queue.

### 4.6 Hold expiry

([0010](../adr/0010-hold-lifetimes-and-payment-expiry.md))

| Phase | Owner | How it works |
|---|---|---|
| Hold before checkout (8 min) | Inventory | Expiry is checked inside the claim: a seat whose `held_until < now()` counts as free to the next claim. A sweeper every ~2 s only tidies up and refreshes availability |
| Checkout (10 min deadline) | Orders | At the deadline: `CancelCheckout` → **Cancelled:** release the hold · **AlreadyCompleted:** wait for the payment · **provider unreachable:** retry |
| Backstop (~35 min) | Inventory | Holds in checkout expire at the provider's session expiry plus a margin |

Cancel the payment first, release the seats second. All expiry decisions use the database clock.

### 4.7 Event cancellation with refunds

```
admin/organizer ─► BackOfficeApi CancelEvent ─► Catalog: Cancelled ─► EventCancelled
 ├─► Inventory    stop new holds, release holds that haven't reached checkout
 ├─► WaitingRoom  close the queue, show a notice
 ├─► Ticketing    revoke all tickets now ─► TicketsRevoked ─► CheckIn door lists
 └─► Orders       EventCancellation process (one per event, tracks progress)
       open checkouts  → CancelCheckout → order Cancelled
       confirmed orders (in pages) → RefundPending ══► Payments.RequestRefund
                                     (stores the request; safe to repeat per order)
                                   ─► OrderCancelled{reason, buyer email} ─► Notifications
     Payments refund worker: rate-limited per provider (Stripe sandbox: 25/s) → refund API → webhook
       → RefundSucceeded ─► order Refunded ─► OrderRefunded ─► Notifications
       → RefundFailed    ─► manual queue in the back office
```

Checkout uses a central coordinator; cancellation lets each module react on its own, plus a
progress tracker. Chargebacks (DisputeOpened → OrderRevoked → tickets revoked) and single-order
refunds from the back office reuse the same path.

---

## 5. Seat inventory and hot events

([0011](../adr/0011-seat-inventory-concurrency.md))

- A hold claims its seats in **one conditional SQL statement** with `FOR UPDATE SKIP LOCKED`:
  locks last one statement, losers get an immediate 409, no deadlocks, no retries.
- One row per sellable place; standing areas are anonymous slot rows, so there is no hot
  counter row.
- The consistency boundary is the seat, not the event.
- Levers for one hot event, in order: waiting-room admission rate → sold-out flags in Valkey →
  one availability snapshot per event → short transactions and connection pooling → Inventory
  on its own database server → (only if measured necessary) batching, then one writer per event.

---

## 6. Waiting room

Our own, because commercial options need paid plans
([0012](../adr/0012-build-our-own-waiting-room.md)):

- a pre-queue shuffled at the on-sale start, then first come first served;
- admission at rate R, which must respect the slowest downstream (Stripe allows 25 requests/s
  per endpoint by default);
- a signed admission token that PublicApi verifies locally;
- if Valkey is lost, the on-sale pauses.

---

## 7. Performance targets

Correctness targets keep their full values; capacity targets get their numbers from a baseline
run in M5 on the reference machine (the project owner's laptop), with lab capacity limits off.
Full definitions and how each is checked: [0015](../adr/0015-performance-targets.md).

| # | Kind | Target (short) |
|---|---|---|
| T1 | capacity | Holds: 99% within 200 ms, < 0.1% errors at the baseline rate |
| T2 | correctness | No seat sold twice, no orphan tickets, every paid order Confirmed or Refunded, no double refunds |
| T3 | correctness | Expired seats claimable instantly; shown available within 10 s; abandoned checkouts released within 60 s of the deadline |
| T4 | capacity | Browse: 99% within 100 ms; primary database CPU flat while browsing |
| T5 | capacity | Waiting room: 99% of joins within 300 ms; admissions within ±5% of R |
| T6 | correctness | Webhook → tickets + email: 95% within 30 s, 99% within 2 min; no webhook lost |
| T7 | capacity | At 3× load: excess rejected within 50 ms; admitted requests still meet T1 |
| T8 | correctness | Offline scan < 300 ms; door list < 5 MB for 80k tickets; revocations reach online devices < 30 s |
| T9 | correctness | 50,000-order cancellation: no double refunds, every order final |
| T10 | correctness | Rolling deploy < 0.1% errors; failover error window < 60 s; no invariant broken |
| T11 | correctness | New instances ready within 60 s after a 3× load step; zero failed requests on scale-in |

---

## 8. Milestones

([0016](../adr/0016-teaching-milestones.md))

| Milestone | Adds | Flows working end to end | Targets |
|---|---|---|---|
| 1a. Modular monolith, CI, tests | Catalog, Organizers (thin), Inventory, Orders, Payments with LocalPay; outbox/inbox/scheduler on PostgreSQL | browse, hold, checkout and payment (LocalPay), hold expiry | T2 in CI |
| 1b. Completing the core | Stripe adapter, Ticketing, Notifications | checkout with Stripe, ticket issuance | T2 in CI |
| 2. Containers, deployment, migrations | Migrator; lab cluster; deploy gate for on-sales; Scaling Lab basics | same flows, deployed | T10 (deploy) |
| 3. High availability, failover | CheckIn + CheckInApi; Valkey caching; database panel in the lab | browse via replicas, check-in, holds during failover | T8, T10 (failover) |
| 4. Async: Kafka, duplicates | Kafka transport; Reporting; refunds, reconciliation, disputes | event cancellation with refunds, issuance at volume | T6, T9 |
| 5. Scaling, load tests | WaitingRoom; autoscaling; capacity settings and presets in the lab | hot-event on-sale through the waiting room | T1, T3, T4, T5, T7, T11 |
| 6. Disaster recovery, runbooks | orphan-payment reconciliation; restore drill | every flow, after a restore | restore drill |

Observability grows in every milestone. Each milestone gets a time-box, agreed at its kickoff.
Detailed plans: [M1a](../milestones/m1a.md).

---

## 9. Repository and environments

One repository, strictly separated
([0021](../adr/0021-product-tooling-platform-separation.md)):

```
product/    the system: src, tests, deploy (chart, values per environment, dashboards, alerts)
tooling/    Scaling Lab, LoadGenerator, LocalPay, k6 load tests, invariant checker, chaos scripts
platform/   local cluster setup: KEDA, CloudNativePG operator, observability, Traefik, Keycloak,
            SeaweedFS, Mailpit
dev/        Aspire AppHost for day-to-day development
docs/       brief, decision log, ADRs, this overview
```

- Nothing under `product/` references `tooling/`; tooling uses only the product's APIs,
  telemetry, deployment labels and documented settings (plus one read-only login for the
  invariant checker).
- The lab environment runs the same product images as any other environment; only its
  configuration differs ([0018](../adr/0018-capacity-from-configuration-not-code.md)).
- Inside `product/src/Modules/`: four projects for each module with a full domain model, two
  for each thin module ([0026](../adr/0026-module-project-layout.md)).
- `product/src/Frontends/` holds the UIs, starting with the fan site. They use the product only
  through its HTTP APIs, like any client ([0049](../adr/0049-fan-site-blazor-server-tokens-on-the-server.md)).
- One container image per host, built by the .NET SDK
  ([0029](../adr/0029-container-image-per-host.md)).
- Trunk-based development: short-lived branches, every pull request runs build, tests and
  architecture tests ([0036](../adr/0036-trunk-based-development.md)).

| Way to run | What | Used for |
|---|---|---|
| Aspire (F5) | product hosts + the fan site + LocalPay + Mailpit, no Kubernetes | day-to-day development |
| Lab | Kubernetes in Docker Desktop (kind mode, 3 nodes), rebuilt from source on every `lab up` | demos, drills, Scaling Lab ([0020](../adr/0020-local-kubernetes-on-docker-desktop.md), [0022](../adr/0022-disposable-lab-environment.md)) |
| CI | kind in GitHub Actions | builds and tests on every push; full system tests nightly |

Object storage uses the S3 API (SeaweedFS locally); secrets are never committed
([0025](../adr/0025-object-storage-and-secrets.md)).

---

## 10. Scaling Lab

A Blazor Server app plus a load generator, in `tooling/lab/`, for this project only
([0017](../adr/0017-scaling-lab-and-load-generator.md)).

```
Scaling Lab ──rate, mix──► LoadGenerator ──HTTP at set rate──► PublicApi × N
     │                                                         Workers × N
     ├──latency──► Toxiproxy (in front of PostgreSQL)          PostgreSQL 1 + R
     │                                                              │ metrics
     ├──── PromQL every 1 s ──── Prometheus ◄── scrape every 1–2 s ─┘
     │                               │
     │                               ▼
     └── Kubernetes API ◄───────── KEDA scales PublicApi, Workers, read replicas
```

- A slider sets requests per second; the screen shows offered, served and rejected requests,
  response times, instances per host (wanted versus ready), database load and a scaling
  timeline.
- One instance handles about 20 requests/s in the lab: a low concurrency limit (a real product
  setting) plus 100 ms of added database latency from Toxiproxy. No simulation code exists in
  the product ([0018](../adr/0018-capacity-from-configuration-not-code.md)).
- What scales on which signal: [0019](../adr/0019-autoscaling-signals.md). Presets demonstrate a
  load step, an on-sale spike, scale-in, a slow database, read-replica scaling, a payment burst,
  and failures under load.
