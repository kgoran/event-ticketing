# M1b: Stripe, tickets and email (draft)

- **Status:** **Draft, not agreed.** Proposed on 2026-10-19; planning paused there. Decisions
  D7–D18 below are open. D7 (a Stripe account) and D18 (team and time-box) need the owner's
  input; the rest have a recommendation to accept or change.
- **Related:** [ADR-0016](../adr/0016-teaching-milestones.md) (milestones), [M1a](m1a.md),
  [payments](../payments.md), [architecture overview](../architecture/overview.md) §4.4

## Where we stopped

M1a is done, including the fan site (U1) and LocalPay's "Back to FanSite" button. D6 kept the
Scaling Lab in M2, as planned. The next step is to settle D7–D18. Once they are agreed, this
draft becomes the M1b plan: the definition of done and the demo script get written, the agreed
decisions become ADRs, and the backlog starts.

## Already decided elsewhere

- M1b = the Stripe adapter, Ticketing and Notifications ([ADR-0016](../adr/0016-teaching-milestones.md)).
- Organizers keeps Keycloak's group membership in step ([ADR-0027](../adr/0027-identity-provider-keycloak.md), update).
- Ticket issuance: `OrderConfirmed` → Ticketing issues one ticket per seat with a signed QR code →
  `TicketsIssued` → the email. "My tickets" reads from Ticketing directly, so fans have their
  tickets even if the email fails (overview §4.4).

## Proposed backlog

Sizes are for one developer: **S** ≤ 2 days, **M** ≤ 1 week, **L** > 1 week.

| ID | Item | Size |
|---|---|---|
| O3 | Order events carry what their consumers need ([ADR-0007](../adr/0007-module-communication.md)): the buyer's email (from the token at checkout) and the event name on `OrderConfirmed` and `OrderRefunded` | S |
| P3 | Stripe adapter: hosted Checkout, expiring the session at the deadline, status, listing, refunds, webhook signature check | L |
| P4 | Choosing the provider per checkout; Aspire wiring for Stripe | S |
| L2 | LocalPay: a timeout or HTTP 500 when creating a payment; webhooks out of order | S |
| TK1 | Ticketing: a signing key per event, tickets issued from `OrderConfirmed` (safe to repeat), signed QR codes, `TicketsIssued` | M |
| TK2 | "My tickets": a PublicApi endpoint, a fan site page with the QR codes, a link from the order page | S |
| N1 | Notifications: Mailpit in Aspire, the "your tickets" and "order refunded" emails, a delivery log so a redelivered event doesn't send twice | M |
| I3 | A per-fan ticket limit per event (default 8, [ADR-0024](../adr/0024-domain-scope-m1-m6.md)) | S |
| G2 | Organizers keeps Keycloak's group membership in step through Keycloak's admin API | M |
| Q5 | T2 extended (no orphan tickets), plus an end-to-end ticket issuance test | S |
| Q6 | M1b demo script, runbook and automated dry run | S |

## Open decisions

| # | Proposal | Alternatives |
|---|---|---|
| D7 | **Stripe access.** The owner creates a free Stripe account (test mode only). Its test keys go in .NET user-secrets. Aspire runs the Stripe CLI as a container to forward webhooks to the laptop. Without keys, Stripe isn't offered and everything runs on LocalPay. CI tests the adapter against `stripe-mock` (Stripe's open-source mock server, no account needed); real Stripe is tested by hand | A public tunnel such as ngrok (needs an account, URLs change); real Stripe in CI with secrets (a public repo can't pass them to pull requests from forks) |
| D8 | **Choosing the provider.** A setting picks the default provider. When more than one is enabled, the fan chooses at checkout | Per event, set by the organizer; per environment only, which can't compare the two in one demo |
| D9 | **QR code format.** A JWS signed with ES256, carrying the ticket ID, event ID, ticket version and the key's ID. Standard, so any scanner library can check it, and it fits a QR code easily | Our own compact binary format: a smaller QR, but non-standard |
| D10 | **Signing keys.** One key pair per event. The private key is stored in Ticketing's own database schema, encrypted with a master key from secrets (an Aspire-generated value locally, a Kubernetes Secret in the lab): envelope encryption, which a cloud key vault can replace later | ASP.NET Core Data Protection (its key ring would need sharing between hosts); a key vault service (paid, or one more thing to run) |
| D11 | **PDF tickets wait until M4.** M1b tickets are QR codes, on "My tickets" and in the email. QuestPDF is free only under $1M company revenue, SeaweedFS is confirmed only in M3, and rendering cost matters at volume, which is M4's topic | PDFsharp/MigraDoc (MIT) plus SeaweedFS now: about one week more |
| D12 | **Email.** SMTP through MailKit (MIT) to Mailpit. The templates are Razor components rendered with .NET's built-in `HtmlRenderer`, so no template engine | `SmtpClient` (Microsoft advises against it for new code); the Scriban or Fluid template engines |
| D13 | **O3's contract change.** Add the fields to version 1 of the events: the change only adds fields, and nothing consumes them yet | A version 2, unnecessary while there are no consumers |
| D14 | **G2 stays, but is cut first.** It has the least demo value, and Keycloak configuration is the known time sink. If time runs short, it moves to M2 | Do it first; or move it now |
| D15 | **LocalPay in M1b: only L2.** Rate limits, latencies sampled from real Stripe calls, and payment listing go to M4, with load testing and reconciliation | All of them now |
| D16 | **The ticket limit (I3) is enforced by Inventory** when a fan holds seats, counting the places already bought plus those held. The "one active hold per fan" rule stops concurrent holds from getting around it | Orders checks at checkout, when the seats are already held |
| D17 | **Not in M1b:** the back-office UI (the seeder and the API are enough); revoking tickets (nothing revokes them before chargebacks and event cancellation in M4); CheckIn (M3); the Scaling Lab (M2) | A minimal back-office UI now |
| D18 | **Team and time-box: the owner's call.** Roughly 6–7 weeks for one developer, about 3 weeks for three. M1a's time-box is still open too | — |

## Risks

- **Stripe needs internet access and the owner's account;** CI uses `stripe-mock` instead.
- **`stripe-mock` only checks request shapes.** It has no state, so LocalPay stays the place for
  behaviour tests.
- **Buyer emails start travelling in events,** which is personal data; ADR-0007 already plans a
  retention limit in M4.

## Still to write once agreed

The scope table, definition of done, demo script and order of work, like [M1a](m1a.md).
