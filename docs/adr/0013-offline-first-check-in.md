# 0013. Offline-first check-in with signed tickets

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0006](0006-ticketing-modules.md) · [0008](0008-hosts.md) · [0024](0024-domain-scope-m1-m6.md)

## Context

Venues often have poor connectivity. When thousands of people arrive at once, the door must
keep working whether or not the scanners can reach the server.

## Decision

Scanners work offline first and sync whenever they can.

```
before doors   scanner ─► CheckInApi GET door list (eventId, since=cursor)
               ◄ ticket ids + revocations + event public key   (~2–4 MB for 80k tickets, no personal data)
at the door    scan → check signature → on the list? not revoked? not already scanned on this device?
               → green/red in < 300 ms, no network needed
when online    every few seconds: POST a batch of scans (safe to repeat, keyed by scanId);
               GET changes since the cursor (new tickets, revocations, scans from other devices)
server         the first accepted scan wins; a second accepted scan → DuplicateEntryDetected (alert)
```

- **Signed QR codes.** Each event has its own signing key pair (e.g. ECDSA P-256, built into
  .NET). Ticketing signs `(ticketId, eventId, version)`; scanners hold only the public key, so
  they can verify a ticket without a network.
- **Tickets bought after the list was downloaded** have a valid signature but aren't on the
  list. A configurable policy decides: accept if the signature is valid (default), reject, or
  call a supervisor.
- **Devices** are registered in the back office, limited to one event, and can be revoked.
- The door list carries no personal data.

This is a deliberate trade-off and the clearest teaching contrast in the project:
**Inventory refuses a sale rather than risk selling a seat twice; the door lets someone in
rather than stop the queue.**

## Alternatives considered

| Alternative | Why not |
|---|---|
| Online-only scanning | Simple, but fails exactly where venues have poor connectivity |
| A venue hub: a local server the devices sync with over the venue's own network | Strongest option, but an extra deployable; a good stretch goal |

## Consequences

- The same ticket (e.g. a screenshot) can get in through two gates while both are offline.
  That is detected afterwards, not prevented.
- A revoked ticket can still be accepted by an offline device until it syncs; for online
  devices, target T8 bounds this to 30 s ([0015](0015-performance-targets.md)).
- The scanner app is an offline-capable web app (PWA) ([0024](0024-domain-scope-m1-m6.md)).
