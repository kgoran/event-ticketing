# Observability: traces and metrics

Everything the product emits goes through OpenTelemetry. ServiceDefaults subscribes to every
source and meter named `EventTicketing.*`, so a new one appears in the Aspire dashboard (and
later in the lab's Grafana) without configuration. This page is the catalogue: add a row when
you add a span or an instrument.

## Traces

**A purchase is one trace**, from `POST /orders` to the confirmation:

```
POST /orders                                       (ASP.NET Core)
└─ command StartCheckout                           Orders, a coordinator
   ├─ command BeginCheckout ┄┄ link ┄┄► POST /events/{id}/holds   (the hold's own trace)
   ├─ command CreateOrder
   └─ command CreateCheckout ─ POST LocalPay /v1/payments
      ...the fan pays...
   payment succeeded ┄┄ link ┄┄► POST /webhooks/localpay           (the webhook's own trace)
   └─ consume payments.payment-succeeded           Workers, from the outbox
      └─ command ConfirmHold                       Inventory, through its module API
```

- **Parent-child** inside a request, and across the outbox: messages carry W3C `traceparent`
  in their headers (ADR-0014), so a consumer's span joins the trace that published the message.
- **Links** between requests: the hold was placed in an earlier request, and the webhook comes
  from the provider. Payments remembers the checkout's trace context with the payment attempt,
  and publishes `PaymentSucceeded` from a span in that trace, linked to the webhook's trace.
- Command spans are named `command <Command>`; message spans `consume <event name>` (outbox) or
  `scheduled <message type>` (scheduler).

| Source | Spans |
|---|---|
| `EventTicketing.Commands` | `command <Command>` for every command, tagged `eventticketing.command` and, on failure, `eventticketing.error.code` |
| `EventTicketing.Messaging` | `consume <event>`, `scheduled <message>`, tagged `messaging.message.id` |
| `EventTicketing.Payments` | `payment succeeded` / `payment failed`: the provider's news, in the checkout's trace |

## Metrics

| Instrument | Type | Tags | What it answers |
|---|---|---|---|
| `eventticketing.commands.handled` | counter | `command`, `outcome` (success, failure, exception), `error.code` on failure | **Hold results** (`PlaceHold`: placed, `hold.seats_unavailable`, `hold.not_on_sale`, `hold.active_hold_exists`) and **checkout outcomes** (`StartCheckout`: started, `checkout.hold_not_active`, `checkout.payment_unavailable`), and every other command |
| `eventticketing.commands.duration` | histogram (s) | `command`, `outcome`, `error.code` | How long handlers take |
| `eventticketing.inventory.places_held` | counter | — | Places claimed by successful holds |
| `eventticketing.inventory.swept` | counter | `swept.kind` (seats, holds) | What the sweeper tidied up (ADR-0010) |
| `eventticketing.orders.transitions` | counter | `order.status` (confirmed, expired, failed, refunding, refunded, refund_failed) | How checkouts end |
| `eventticketing.payments.webhooks` | counter | `payment.provider`, `webhook.result` (stored, duplicate, invalid_signature) | Webhook traffic, and duplicates dropped |
| `eventticketing.payments.outcomes` | counter | `payment.status` (succeeded, failed, cancelled) | What providers report |
| `eventticketing.payments.refunds` | counter | `refund.status` (succeeded, failed) | Refunds, which should be rare (ADR-0009) |
| `eventticketing.messages.delivered` / `.retried` / `.dead_lettered` / `.duplicates` | counter | `message.type` | Outbox and scheduler health (ADR-0014) |
| `eventticketing.messages.lag` | histogram (s) | `message.type` | Time from an event occurring to its delivery |

All tag names start with `eventticketing.` (shortened in the table). Tags only take values from
small, fixed sets: never IDs, user names or amounts.

## Seeing it

Start the AppHost and open the dashboard: **Traces** shows a purchase as one trace (filter by
`command StartCheckout`); **Metrics** lists the instruments above per resource.
