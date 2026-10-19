# 0046. LocalPay: misbehaviour by amount, and its contract checked in CI

- **Status:** Agreed
- **Date:** 2026-10-19
- **Related:** [0004](0004-payment-providers-stripe-and-localpay.md) · [0021](0021-product-tooling-platform-separation.md) · [0037](0037-kiota-api-clients.md) · [0042](0042-payment-provider-contract-and-webhooks.md) · [payments.md](../payments.md) · design-decisions §14 (L1-a…f)

## Context

[payments.md](../payments.md) asks LocalPay to behave like a hosted checkout and misbehave on
demand, "chosen by special amounts or card numbers". The product's adapter is written against
LocalPay's published API document, not its code ([0021](0021-product-tooling-platform-separation.md)).

## Decision

- **Scenarios are chosen by the last two digits of the amount** in minor units. The checkout
  page names the active scenario.

  | Ends in | LocalPay |
  |---|---|
  | .01 | approving declines |
  | .02 | sends the webhook late (10 s by default) |
  | .03 | sends the webhook twice, same event ID |
  | .04 | sends no webhook |
  | .05 | completes the payment at the moment the shop expires it |

- **Webhooks** are delivered in the background and retried until the shop answers 2xx (5
  attempts, doubling from 1 s). A retry or duplicate repeats the event ID.
- **Expire** answers 200 when the payment is expired (also when repeated) and 409 with the
  payment when it already succeeded or failed, as Stripe refuses to expire a completed session.
- **Refunds** succeed at once, may be partial, take an Idempotency-Key and send `refund.succeeded`.
- **Headless mode** approves new payments after a random delay, for load tests.
- Not in M1a: timeouts and 500s on create, rate limits, sampled latencies, payment listing,
  out-of-order webhooks (M1b/M4).
- **CI checks the contract**: `tooling-ci` runs LocalPay's tests and fails if its OpenAPI document
  or the generated product clients are out of date.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Scenarios by card number | The checkout page has no card entry; amounts also work headless |
| Scenarios by header or configuration | Not reachable from the product's normal flow |
| Asynchronous (pending) refunds | Not needed for M1a's single refund; can come with M4's mass refunds |

## Consequences

- Tests and demos trigger a scenario through prices, and must hold **one** seat: two seats at
  25.03 make 50.06, which is no scenario.
- Product tests can't use LocalPay itself; they fake its published API at the HTTP level
  ([0048](0048-test-layers-and-demo-dry-run.md)).
