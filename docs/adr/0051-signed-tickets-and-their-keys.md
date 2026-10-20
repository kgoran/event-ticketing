# 0051. Signed tickets: JWS QR codes, a key per event, envelope encryption

- **Status:** Agreed
- **Date:** 2026-10-20
- **Related:** [0006](0006-ticketing-modules.md) · [0007](0007-module-communication.md) · [0013](0013-offline-first-check-in.md) · [0025](0025-object-storage-and-secrets.md) · [M1b plan](../milestones/m1b.md) (D9, D10, D11)

## Context

[0013](0013-offline-first-check-in.md) decided that each event has its own signing key pair and
that scanners verify tickets offline with the public key. M1b issues the tickets, so the code's
format, where the private keys live, and what a ticket looks like had to be settled.

## Decision

**The code**

- A ticket's QR code is a **JWS in compact form, signed with ES256** (ECDSA P-256, built into
  .NET). Header: `alg` and `kid`. Payload: the ticket ID, the event ID and the ticket's version.
  No personal data.
- Any JOSE library can verify it, so the scanner (M3) and any test can check a ticket without our
  code. The code is about 250 characters, which fits a QR code easily.

**The keys**

- Ticketing creates a key pair per event when it sees `EventPublished`.
- The private key is stored in Ticketing's own schema, **encrypted with a master key** read from
  secrets (envelope encryption, AES-GCM). Locally the master key is an Aspire parameter generated
  per run; in the lab, a Kubernetes Secret ([0025](0025-object-storage-and-secrets.md)).
- The key ID in every code allows rotation later without invalidating issued tickets.

**The tickets**

- On `OrderConfirmed`, one ticket per seat, unique per order and seat, so a redelivered event
  issues nothing twice. `TicketsIssued` carries the codes, the buyer's email and the event name,
  so Notifications can email them without calling back.
- **No PDFs until M4.** M1b tickets are QR codes on "My tickets" and in the email. PDFs come with
  object storage when ticket issuance at volume is the topic.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Our own compact binary format | A smaller QR, but every verifier needs our code |
| Data Protection for the private keys | Its key ring would have to be shared by every host that signs |
| A key vault service | Paid or one more thing to run; envelope encryption is the same pattern and swaps for one later |
| PDF tickets now (QuestPDF) | QuestPDF is free only under $1M company revenue; PDFsharp would do, but object storage is confirmed only in M3 |

## Consequences

- Losing the master key makes every stored private key useless; issued tickets still verify with
  the public keys. In the disposable lab that is the normal case.
- Ticket codes are bearer codes and travel in `TicketsIssued`: topic retention and access matter
  (M4, with the buyer emails).
- The scanner app needs only the event's public key and a JOSE library.
