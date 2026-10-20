# 0050. The Stripe adapter, built and tested without a Stripe account

- **Status:** Agreed
- **Date:** 2026-10-20
- **Related:** [0004](0004-payment-providers-stripe-and-localpay.md) · [0005](0005-no-paid-services-local-first.md) · [0042](0042-payment-provider-contract-and-webhooks.md) · [M1b plan](../milestones/m1b.md) (D7, D8, D19)

## Context

[0004](0004-payment-providers-stripe-and-localpay.md) puts two providers behind one interface,
Stripe and LocalPay, so the interface can't quietly take the shape of the one we wrote. M1b builds
the Stripe adapter, but nobody on the project has a Stripe account, and the demo must run on one
laptop without internet.

## Decision

- **The adapter is built now** with Stripe's official .NET library (`Stripe.net`): hosted Checkout
  Sessions, expiring a session at the checkout deadline, status, listing, refunds, and the webhook
  signature check.
- **It is tested without an account:**
  - Calls are contract-tested against **`stripe-mock`**, Stripe's open-source mock server, run
    as a container by the tests. It checks each request against Stripe's API description and
    answers with fixtures.
  - The webhook signature check is tested with payloads the tests sign themselves, following
    Stripe's documented scheme (`t=…,v1=…`, HMAC-SHA256 over `timestamp.payload`).
- **Stripe is off unless configured.** With no secret key, the adapter isn't registered, and
  LocalPay is the only provider. A setting picks the provider for new checkouts
  (`Payments:Provider`, default `localpay`); the fan chooses only when more than one is enabled.
- **Turning real Stripe on later** needs test keys in user-secrets and the Stripe CLI forwarding
  webhooks; this is documented, not built.
- The demo and the system tests use LocalPay only.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Leave Stripe out until someone has an account | Keeps the interface untested against a second, real provider shape; the payments developer would have little to do in M1b |
| Real Stripe in CI with secrets | A public repository can't pass secrets to pull requests from forks; the sandbox's rate limit is shared |
| Hand-written HTTP calls instead of `Stripe.net` | More code to maintain and to get wrong; the library is Stripe's own and handles signature checks and API versions |

## Consequences

- The adapter's request shapes are checked in CI; its behaviour (session lifecycle, webhooks
  in real order) is proven only when someone runs it against Stripe's test mode.
- `stripe-mock` has no state: tests check what we send and how we read answers, not flows.
- Nothing in the demo depends on the internet.
