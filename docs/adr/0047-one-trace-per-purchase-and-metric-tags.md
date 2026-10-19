# 0047. One trace per purchase; metrics from commands and business events

- **Status:** Agreed
- **Date:** 2026-10-19
- **Related:** [0009](0009-checkout-without-shared-transaction.md) · [0014](0014-outbox-inbox-scheduler-on-postgresql.md) · [0033](0033-command-pipeline.md) · [observability.md](../observability.md) · design-decisions §14 (Q3-a…c)

## Context

The M1a definition of done asks for one trace showing the whole purchase, and the first
business metrics. A purchase spans several requests (the hold, the checkout, the provider's
webhook) and asynchronous work in Workers.

## Decision

- **A purchase is one trace, from `POST /orders` to the confirmation.** Inside a request, spans
  are parent and child; across the outbox, messages carry W3C `traceparent` ([0014](0014-outbox-inbox-scheduler-on-postgresql.md)).
- **Across requests, links, not parents.** Payments stores the checkout's trace context with the
  payment attempt and publishes `PaymentSucceeded` from a span in that trace, linked to the
  webhook's own trace. `BeginCheckout` links the trace of the request that placed the hold.
- **Metrics:** the command metric carries the error code on failure, which gives hold results
  and checkout outcomes (and every other command's) without per-command counters. Module meters
  add what isn't a command: order transitions, payment outcomes, webhooks, refunds, places held,
  sweeps.
- **Tags take values only from small, fixed sets**: command names, statuses, error codes. Never
  IDs, user names or amounts.
- `docs/observability.md` is the catalogue; a new span or instrument adds a row.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Parenting the webhook's request under the checkout | Pretends one request caused the other; the provider's call starts its own trace |
| A counter per business outcome in each handler | Duplicates what the command metric already sees |
| No link to the hold | The purchase would read from checkout onwards only |

## Consequences

- The Aspire dashboard shows the purchase as one trace with two links. A test checks the shape
  with an `ActivityListener`.
- Error codes are part of the metrics contract: renaming one changes dashboards.
