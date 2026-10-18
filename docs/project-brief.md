# Project brief

## Purpose

A sample system the team builds together to learn the concerns that most projects share,
instead of only ever seeing the slice one project happens to need. The domain is chosen so
that anyone understands it in five minutes, while it still has one genuinely hard problem
and real (not invented) reasons for high availability and scaling.

## What the project must teach

- CI/CD: automated build, test and deployment pipelines; environment separation
- High availability: multiple instances across availability zones, health checks, failover
- Database replication: primary + replicas, failover, replication lag, read/write routing
- Horizontal scaling: stateless services, autoscaling, caching, what does *not* scale out
- Concurrency and consistency: no seat sold twice, idempotency, optimistic/pessimistic locking
- External integrations: a payment provider with webhooks, retries, reconciliation
- Asynchronous processing: outbox, message broker, duplicate-safe consumers, sagas
- Performance requirements: explicit targets, load tests that verify them
- Deployment requirements: zero-downtime releases, database migrations without downtime,
  feature flags, deployment windows
- Observability: logs, metrics, traces, alerting on business failures

## Why event ticketing

Compared candidates: event ticketing, parcel tracking, multi-tenant booking SaaS,
P2P wallet, online auctions (e-commerce left out: Microsoft's eShop sample already covers it
and the domain keeps growing).

Ticketing was chosen because it covers the most concerns well:

- **Hard core problem:** on-sale spikes where thousands of people compete for the same seats.
  A popular event is a single hot spot everyone wants at once.
- **Read-heavy and write-contended at the same time:** browsing is cacheable
  (cache, CDN, read replicas); buying fights over the same rows.
- **Traffic spikes of 50–100× normal** at on-sale time: autoscaling, rate limiting,
  a virtual waiting room, load shedding.
- **A real deployment requirement:** no deploys during an on-sale.
- **Simple integrations:** a payment provider and email.

## Domain scope

Core flow:

```
organizer creates event → fans browse → fan holds seats → fan pays
  → tickets issued (PDF + QR) → staff scan tickets at the door
```

Actors: organizer, fan, door staff, back-office admin.

Candidate modules (starting point for the next design step, **not agreed yet**):
Events (venues, seat maps, event catalog), Inventory (seat availability and holds),
Orders, Payments, Ticketing (issue, QR, check-in), Notifications, Organizers,
Reporting, Waiting room.

**Agreed (2026-10-18):** ten modules replace this draft — see
[ADR-0006](adr/0006-ticketing-modules.md) and the [architecture overview](architecture/overview.md).

## Integrations

| Integration | Purpose | Status |
|---|---|---|
| Stripe (test mode) | Real payment provider: hosted checkout, webhooks, refunds | Agreed |
| LocalPay (our own simulator) | Second provider: load tests, fault injection, offline/CI | Agreed |
| Email (Mailpit locally, a real provider later) | Ticket delivery, notifications | Proposed |
| Organizer API + outbound webhooks | Teaches signed webhooks, retries, replay, public API, rate limits | Out of M1–M6 ([ADR-0024](adr/0024-domain-scope-m1-m6.md)) |
| Domestic PSP (e.g. Monri, CorvusPay) | Third adapter, if a real local provider is wanted | Optional |

Details: `payments.md`.

## Example performance targets (to be confirmed)

- Hold a seat in under 200 ms for 99% of requests at 5,000 requests/s.
- Zero seats sold twice, verified automatically after every load test.
- Expired holds released within 10 s of expiry.

Superseded by [ADR-0015](adr/0015-performance-targets.md): targets T1–T11, with the traffic,
data and machine they are measured on.

## Teaching milestones (proposed)

1. Modular monolith, CI pipeline, unit and integration tests
2. Containers, deployment pipeline, environments, database migrations without downtime
3. High availability: replicas, a failover drill
4. Asynchronous processing: outbox, Kafka, consumers that handle duplicates
5. Scaling: load tests against the targets, autoscaling
6. Disaster-recovery drill and runbooks

Mapped to modules, flows and targets in [ADR-0016](adr/0016-teaching-milestones.md).

## Non-goals

- Real money, real regulation, production operation.
- Paid services of any kind: everything runs locally
  ([ADR-0005](adr/0005-no-paid-services-local-first.md)).
- A full-featured seat-map designer or marketing site.
