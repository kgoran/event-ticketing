# Observability: traces and metrics

Everything the product emits goes through OpenTelemetry. ServiceDefaults subscribes to every
source and meter named `EventTicketing.*`, so a new one appears in the Aspire dashboard and in
the lab's Grafana without configuration. This page is the catalogue: add a row when
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
- **Background loops are not traced on their own.** A database or Valkey call that belongs to no
  request and no message (the outbox's, the schedule's and the availability snapshots' polling,
  every second) is not exported (`BackgroundCallsFilter` in ServiceDefaults): in the lab they were
  95% of all traces. The same calls inside a request or a message are, as its children.

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
| `eventticketing.tickets.issued` | counter | — | Tickets issued for confirmed orders, one per place (ADR-0051) |
| `eventticketing.tickets.revoked` | counter | — | Tickets revoked, which the door then refuses (D63) |
| `eventticketing.checkin.scans` | counter | `scan.outcome` (admitted, duplicate, refused) | What the doors uploaded: admissions, duplicates (raised as DuplicateEntryDetected), turned away (ADR-0013) |
| `eventticketing.checkin.devices` | counter | `device.change` (registered, revoked) | Door devices registered and revoked (D61) |
| `eventticketing.notifications.emails` | counter | `email.kind` (tickets, refund), `email.result` (sent, no_address) | Emails sent to fans (ADR-0052) |
| `eventticketing.orders.transitions` | counter | `order.status` (confirmed, expired, failed, refunding, refunded, refund_failed) | How checkouts end |
| `eventticketing.payments.webhooks` | counter | `payment.provider`, `webhook.result` (stored, duplicate, invalid_signature) | Webhook traffic, and duplicates dropped |
| `eventticketing.payments.outcomes` | counter | `payment.status` (succeeded, failed, cancelled) | What providers report |
| `eventticketing.payments.refunds` | counter | `refund.status` (succeeded, failed) | Refunds, which should be rare (ADR-0009) |
| `eventticketing.messages.delivered` / `.retried` / `.dead_lettered` / `.duplicates` | counter | `message.type` | Outbox and scheduler health (ADR-0014) |
| `eventticketing.messages.lag` | histogram (s) | `message.type` | Time from an event occurring to its delivery |

All tag names start with `eventticketing.` (shortened in the table). Tags only take values from
small, fixed sets: never IDs, user names or amounts. A histogram in seconds names its buckets
(`HistogramBuckets`): OpenTelemetry's defaults are for milliseconds, and with them every percentile
read about 5 s (found on the lab's dashboards; an architecture test now checks it).

## Under load: sampling

The Scaling Lab's load generator traces 1 in 20 of its requests and passes the decision on in
`traceparent`. The product's hosts sample parent-based (OpenTelemetry's default), so they record
the same 1 in 20: the dashboard keeps a representative sample instead of hundreds of traces a
second. Metrics are not sampled; they count every request. A request from a fan or a test has no
sampled-out parent, so it is always traced.

## Seeing it

**Under Aspire:** start the AppHost and open the dashboard: **Traces** shows a purchase as one
trace (filter by `command StartCheckout`); **Metrics** lists the instruments above per resource.

**In the lab** (D55, [docs/lab.md](lab.md#seeing-it-grafana-d55)): Grafana at
http://localhost:3000, fed by the OpenTelemetry Collector. The dashboards are in
`product/deploy/chart/dashboards/`: **Services** (per host), **Purchases** (the instruments above),
**Database** (CloudNativePG and PgBouncer). In Prometheus a name loses its dots and gains its unit:
`eventticketing.commands.handled` is `eventticketing_commands_handled_total`, the tag
`eventticketing.command` is the label `eventticketing_command`; `service.name` is the label `job`
and `service.instance.id` (the pod) is `instance`. Logs in Loki carry `service_name`, and
`trace_id` as structured metadata: `{service_name="publicapi"} | trace_id="…"` finds a trace's
lines, which is what Tempo's **Logs for this span** runs.

**Adding a panel:** add the instrument (and its row above), then the panel in Grafana; export the
dashboard's JSON over its file. A panel's description says what it shows and how to read it.
