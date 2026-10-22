# 0056. Check-in: devices, the scanner, and revocation in M3

- **Status:** Agreed (decided overnight for the owner's review, D60–D64)
- **Date:** 2026-10-21
- **Related:** [0013](0013-offline-first-check-in.md) · [0015](0015-performance-targets.md) (T8) · [0027](0027-identity-provider-keycloak.md) · [0035](0035-blazor-for-all-uis.md) · [0051](0051-signed-tickets-and-their-keys.md) · [M3 plan](../milestones/m3.md)

## Context

ADR-0013 agreed how check-in works offline: door lists, local decisions, scans uploaded later,
duplicates detected afterwards. Three things were left for M3: how a device proves who it is,
what the scanner is built with, and where revocations come from before M4's chargebacks.

## Decision

- **CheckIn (D60)** follows ADR-0013: a module owning door lists, devices, the scan log and
  duplicates; **CheckInApi** at http://localhost:5104 serves the door list (ticket IDs,
  revocations and the event's public keys, by cursor) and takes scan batches, idempotent by scan
  ID. The first accepted scan of a ticket wins; a second is a `DuplicateEntryDetected`.
  BackOfficeApi registers and revokes devices.
- **Device identity (D61):** each registered device is a Keycloak client (client credentials),
  created and deleted by Organizers' Keycloak adapter, its token limited to one event and the
  `door-staff` role.
- **The scanner (D62)** is a Blazor WebAssembly app installed as a PWA, served by CheckInApi. Once
  loaded it works offline: the door list and the scans wait in the browser's storage. It reads QR
  codes with the camera through a vendored JavaScript decoder (jsQR, Apache-2.0); a code can also
  be typed or pasted.
- **Revocation (D63):** an admin can revoke a ticket in BackOfficeApi (Ticketing), which publishes
  `TicketsRevoked`; M4's chargebacks and cancellations use the same path.
- **T8 (D64)** is measured with a device simulator in `tooling/lab`, like the load generator.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Device tokens issued by CheckIn | Our own key management and revocation, where Keycloak has both |
| A native scanner app | A mobile toolchain for a teaching lab; a PWA shows the same offline design |
| The browser's `BarcodeDetector` | Not on every desktop browser |
| k6 for T8 | Another runtime; the lab's load is .NET |

## Consequences

- Every device is a client in the realm; deleting the device deletes the client.
- The scanner can be shown on a laptop without a camera.
- T8 becomes measurable in M3, revocations included.

## Updates

- **2026-10-22 (M3 CI3, D70, D71, decided while building):** Devices are created by **CheckIn's own
  Keycloak adapter**, with its own service account (`eventticketing-checkin`, allowed to manage
  clients only), not by Organizers' adapter: CheckIn may depend only on Catalog and Ticketing. A
  device's client puts the role `door-staff`, `event_id` and `device_id` into every token as fixed
  claims. CheckIn makes the device's secret, sends it to Keycloak with the new client and shows it
  once. CheckInApi refuses a revoked device at once, whatever token it still holds. The door list
  is a log of changes (issued, revoked, admitted) whose sequence is the devices' cursor; its writes
  take a per-event lock so that sequence order is commit order.

