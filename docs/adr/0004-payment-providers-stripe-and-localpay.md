# 0004. Two payment providers: Stripe test mode and our LocalPay simulator

- **Status:** Agreed
- **Date:** 2026-10-18 (agreed in principle earlier; recorded here)
- **Related:** [0009](0009-checkout-without-shared-transaction.md) · [0010](0010-hold-lifetimes-and-payment-expiry.md) · [0021](0021-product-tooling-platform-separation.md) · [payments.md](../payments.md)

## Context

The project needs a real payment provider with hosted checkout, webhooks, refunds and
reconciliation. It also needs payment behaviour it can control for load and failure tests.

Checked against Stripe's documentation on 2026-10-18:

- Sandbox (test mode) rate limit: 25 requests/s for the whole account (live mode: 100), and
  25 requests/s per endpoint by default.
- Stripe advises against load testing in a sandbox and recommends a mock whose latencies are
  sampled from real calls.

## Decision

- Two providers behind one `IPaymentProvider` interface (interface in the Payments module's
  Application layer, adapters in Infrastructure):
  - **Stripe** in test mode, hosted Checkout.
  - **LocalPay**, our own simulator in `tooling/simulators/LocalPay`.
- LocalPay behaves like a real provider: hosted payment page, redirect back, HMAC-signed
  webhooks, status and refund APIs, session expiry and early expiry, listing payments by time
  range. It plays failure scenarios on demand: decline; webhooks delayed, duplicated, out of
  order or missing; timeouts and HTTP 500s; a payment completing while we expire the session;
  rate limits; latencies sampled from real Stripe calls. A headless mode approves payments
  automatically for load tests.
- Card and wallet payments only ([0010](0010-hold-lifetimes-and-payment-expiry.md)).
- A domestic provider (for example Monri or CorvusPay) can be added later as a third adapter.
- The payment flow and failure handling are in [payments.md](../payments.md).

## Alternatives considered

| Alternative | Why not |
|---|---|
| Stripe only | Sandbox limits make load tests impossible; Stripe can't misbehave on demand; the interface would quietly take Stripe's shape |
| A generic HTTP mock (e.g. WireMock) instead of LocalPay | Cheaper, but no hosted page, no signed webhooks, no stateful scenarios |
| A domestic provider instead of Stripe | Stripe's test mode and documentation are better for teaching; a domestic provider stays an optional third adapter |

## Consequences

- LocalPay is tooling, not product ([0021](0021-product-tooling-platform-separation.md)).
  To the product it is an external provider like Stripe: the product's adapter is written
  against LocalPay's published API document, not its code.
- No real money is involved anywhere.
