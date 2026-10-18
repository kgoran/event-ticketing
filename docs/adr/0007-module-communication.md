# 0007. Synchronous calls only from Orders; events everywhere else

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0002](0002-module-boundary-rules.md) · [0006](0006-ticketing-modules.md) · [0009](0009-checkout-without-shared-transaction.md) · [0014](0014-outbox-inbox-scheduler-on-postgresql.md)

## Context

Chains of synchronous calls between modules couple their availability and hide
dependencies. Some calls are unavoidable, because the caller needs an answer from the module
that owns the data before it can continue.

## Decision

1. **A synchronous call is allowed only where the caller needs a decision from the owning
   module to continue.** There are exactly two such pairs:

   | Caller → owner | Calls |
   |---|---|
   | Orders → Inventory | `BeginCheckout`, `ConfirmHold`, `ReleaseHold` |
   | Orders → Payments | `CreateCheckout`, `CancelCheckout`, `RequestRefund` |

   Orders is the only module that coordinates others, and nothing calls back into Orders
   synchronously, so there are no chains and no cycles.
2. **Everything else is an integration event** (outbox → transport → inbox).
3. **Events carry the data their consumers need.** For example, `OrderCancelled` includes the
   buyer's email and the event name, so Notifications never has to call back for them.
4. **Pages that need data from two modules are assembled by the client.** For example, the
   event page combines Catalog data with Inventory's availability. Modules don't call each
   other to build pages.

Event catalogue:

| Publisher | Events | Consumers |
|---|---|---|
| Catalog | `EventPublished`, `PricesChanged`, `OnSaleScheduled`, `EventCancelled` | Inventory, Orders, WaitingRoom, Ticketing, CheckIn, Reporting |
| Inventory | `SoldOut` | WaitingRoom |
| Payments | `PaymentSucceeded`, `PaymentFailed`, `RefundSucceeded`, `RefundFailed`, `DisputeOpened` | Orders |
| Orders | `OrderConfirmed`, `OrderCancelled`, `OrderRefunded`, `OrderRevoked` | Ticketing, Notifications, Reporting |
| Ticketing | `TicketsIssued`, `TicketsRevoked` | CheckIn, Notifications, Reporting |
| CheckIn | `TicketCheckedIn`, `DuplicateEntryDetected` | Reporting |
| Organizers | `OrganizerCreated` | Reporting |

Organizer membership travels in the login token, so checking it needs no call to Organizers.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Thin events plus callbacks to fetch details | Simpler events, but every consumer then depends on the producer being up |
| A mediator for calls between modules | Hides dependencies (design-decisions §6) |
| More synchronous calls (e.g. Notifications asking Orders for the buyer's email) | Couples availability; builds the call chains this ADR exists to prevent |

## Consequences

- Downstream modules are eventually consistent.
- Personal data (buyer emails) ends up in event topics, so topics get a retention limit (set in M4).
- Adding a consumer needs no change to the producer.
- Adding a new synchronous call means amending this ADR.

## Updates

- **2026-10-18 (S2):** integration events identify the message with `MessageId`, not `EventId`:
  in this codebase "event" means a ticketed event, and `EventId` always refers to one. The module
  APIs exist as `IInventoryApi` and `IPaymentsApi`, each implemented in-process by running the
  module's own commands through the pipeline.
