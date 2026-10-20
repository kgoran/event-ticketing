# M1b: tickets, email and the Stripe adapter

- **Status:** Agreed 2026-10-20 (decisions D7–D19). Time-box: **3 weeks**, team of five.
  M1a is closed as done. **Built 2026-10-20**, every backlog item including G2: O3 #26, TK1 #27,
  TK2 #28, I3 #29, N1 #30, L2 #31, P3 #32, G2 #33, Q5 #34, Q6 (runbook `docs/demos/m1b.md`).
- **Related:** [ADR-0016](../adr/0016-teaching-milestones.md) (milestones), [M1a](m1a.md),
  [payments](../payments.md), [architecture overview](../architecture/overview.md) §4.4,
  ADRs [0050](../adr/0050-stripe-adapter-without-an-account.md),
  [0051](../adr/0051-signed-tickets-and-their-keys.md), [0052](../adr/0052-email-through-mailpit.md)

## Goal

A paid order becomes tickets, and the fan gets them: **pay → tickets issued with signed QR codes
→ "My tickets" in the fan site → the tickets by email.** The Stripe adapter is built and
contract-tested without a Stripe account. The demo runs on one laptop, without internet, with one
presenter.

## Scope

| In M1b | Not yet |
|---|---|
| Ticketing: a signing key per event, one ticket per seat, signed QR codes (JWS), `TicketsIssued` | PDF tickets and object storage (M4, [ADR-0051](../adr/0051-signed-tickets-and-their-keys.md)); revoking tickets (M4: chargebacks and event cancellation) |
| "My tickets" in PublicApi and the fan site | CheckIn and the scanner (M3) |
| Notifications: the "your tickets" and "order refunded" emails, through Mailpit | Other channels (SMS, push) |
| Stripe adapter, tested against `stripe-mock` ([ADR-0050](../adr/0050-stripe-adapter-without-an-account.md)) | Real Stripe in the demo or in CI; the fan choosing a provider (once Stripe is enabled) |
| LocalPay: a timeout or HTTP 500 when creating a payment; webhooks out of order | Rate limits, sampled latencies, payment listing (M4) |
| A per-fan ticket limit per event (default 8) | — |
| Organizers keeps Keycloak's group membership in step (cut first if time runs short) | — |
| — | The back-office UI; the Scaling Lab (M2; its basics were built right after M1b, D20) |

## Order of work

```
O3 order events ─► TK1 tickets ─► TK2 my tickets ─┐
                        └────────► N1 email ──────┼─► Q5 T2 with tickets ─► Q6 demo
P3 Stripe adapter, L2 LocalPay, I3 limit, G2 Keycloak (in parallel) ─┘
```

## Backlog

Sizes are for one developer: **S** ≤ 2 days, **M** ≤ 1 week, **L** > 1 week.

| ID | Item | Done when | Depends on | Size |
|---|---|---|---|---|
| O3 | Order events carry what their consumers need ([ADR-0007](../adr/0007-module-communication.md)): the buyer's email (from the token at checkout) and the event name on `OrderConfirmed` and `OrderRefunded`, added to version 1 | Consumers need no call back to Orders | — | S |
| TK1 | Ticketing module: a signing key pair per event, created on `EventPublished` and stored under envelope encryption; on `OrderConfirmed`, one ticket per seat with a signed QR code, safe to repeat; `TicketsIssued` | A paid order has exactly one ticket per seat; a code verifies with the event's public key and a tampered one doesn't (tests) | O3 | M |
| TK2 | "My tickets": PublicApi endpoint; fan site page with the QR codes; a link from the order page | A fan sees their tickets seconds after paying | TK1 | S |
| N1 | Notifications module: Mailpit in Aspire; the "your tickets" email (with QR codes) and the "order refunded" email; templates as Razor components; a delivery log | A redelivered event sends no second email (tests); the emails arrive in Mailpit | O3, TK1 | M |
| I3 | Per-fan ticket limit per event (default 8, [ADR-0024](../adr/0024-domain-scope-m1-m6.md)), enforced by Inventory when a fan holds places | A hold that would take a fan past the limit is refused (tests) | — | S |
| L2 | LocalPay: a timeout or HTTP 500 when creating a payment, chosen by amount; webhooks out of order | The product's retry with the same idempotency key ends with one payment (tests) | — | S |
| P3 | Stripe adapter: Checkout Sessions, expiring them at the deadline, status, listing, refunds, webhook signature check; registered only when Stripe is configured | Contract tests against `stripe-mock` and signature tests pass in CI | — | L |
| G2 | Organizers keeps Keycloak's group membership in step, through Keycloak's admin API, from the outbox | Adding a member grants access at their next sign-in (system test) | — | M |
| Q5 | T2 extended: no orphan tickets, every Confirmed order has exactly one ticket per seat | Passes in CI | TK1 | S |
| Q6 | M1b demo script, runbook (with a "day before" checklist), automated dry run | The dry run passes | all | S |

**Splitting the work** (five people, after O3):

1. Ticketing: O3, TK1.
2. "My tickets" and the fan site: TK2, I3.
3. Notifications: N1.
4. Payments: P3, L2.
5. Identity, tests and demo: G2, Q5, Q6.

## Definition of done

1. Buying in the fan site issues one ticket per seat with a signed QR code; the fan sees them in
   "My tickets" within seconds and receives them by email (in Mailpit).
2. Ticket codes verify with the event's public key; a tampered code fails (tests).
3. The per-fan ticket limit holds (tests).
4. The Stripe adapter passes its contract tests against `stripe-mock` in CI; LocalPay's new
   scenarios are covered.
5. A member added in Organizers can act for the organizer after signing in again (system test),
   unless G2 was cut.
6. T2 holds with tickets: no seat sold twice, no orphan tickets, every paid order Confirmed or
   Refunded.
7. The demo runs offline on one laptop; its dry run passes in CI.
8. Every new decision has an ADR.

## Demo script (about 15 minutes, one presenter)

1. Start the AppHost in demo mode; the dashboard shows Mailpit next to the rest.
2. Seed the demo.
3. Ana (normal window) buys two seats; the order page goes Confirmed and links to her tickets.
4. "My tickets": one QR code per seat. Show what's inside one: a signed token naming the ticket,
   the event and the key.
5. Mailpit: the "your tickets" email with the same codes.
6. Dashboard → Traces: payment succeeded → order confirmed → tickets issued → email sent.
7. Ana tries to hold seven more seats: refused, the limit is eight per event.
8. Organizers: adam adds ben to the demo organizer; ben signs in again and can manage its events.
9. A pull request: the Stripe adapter's contract tests against `stripe-mock` among the checks.

## Risks

- **`stripe-mock` only checks request shapes.** It has no state and no hosted page, so the
  adapter's behaviour is proven against real Stripe only when someone enables it
  ([ADR-0050](../adr/0050-stripe-adapter-without-an-account.md)).
- **Buyer emails now travel in events**, which is personal data; ADR-0007 plans a retention
  limit in M4. Ticket codes travel in `TicketsIssued` for the email: they are bearer codes, so
  the same applies.
- **Keycloak's admin API (G2) is the known time sink.** It is cut first.
