# Payments

**Status: Agreed** — [ADR-0004](adr/0004-payment-providers-stripe-and-localpay.md): two
payment providers behind one interface, Stripe (test mode) and LocalPay, a simulator we write
ourselves. A domestic provider (e.g. Monri, CorvusPay) can be added later as a third adapter.
**Amended 2026-10-18** by [ADR-0009](adr/0009-checkout-without-shared-transaction.md) (no
transaction spans modules) and [ADR-0010](adr/0010-hold-lifetimes-and-payment-expiry.md)
(hold lifetimes, cancel the payment before releasing seats).

## Why our own provider as well as Stripe

- **Load tests can't use Stripe.** Stripe's sandbox allows 25 requests/s for the whole account
  and 25 requests/s per endpoint by default (live mode: 100 requests/s overall). Stripe itself
  advises against load testing in a sandbox and recommends a mock with latencies sampled from
  real calls. (Checked against Stripe's documentation on 2026-10-18.)
- **Stripe won't misbehave on demand.** Tests need duplicate, late and missing webhooks,
  and timeouts, whenever they call for them.
- **Offline development and CI** without real API keys.
- **Two implementations keep the interface honest** — with only Stripe, it quietly takes
  on Stripe's shape.

## Interface (sketch)

Lives in the Payments module's `Application` layer; adapters live in `Infrastructure`.

```csharp
public interface IPaymentProvider
{
    Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken ct); // idempotency key = order ID
    Task<CancelPaymentResult> CancelPaymentAsync(string providerPaymentId, CancellationToken ct);     // Cancelled | AlreadyCompleted (Stripe: expire the Checkout Session)
    Task<PaymentStatus> GetStatusAsync(string providerPaymentId, CancellationToken ct);              // used by reconciliation
    IAsyncEnumerable<ProviderPayment> ListPaymentsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct); // reconciliation: payments with no order
    Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct);
    WebhookEvent VerifyWebhook(string payload, IReadOnlyDictionary<string, string> headers);         // signature check
}
```

Only card and wallet payments are offered. Methods that settle days later (SEPA Direct Debit,
some bank redirects) would complete long after a hold has expired.

## LocalPay simulator

Location: `tooling/simulators/LocalPay` (tooling, not product —
[ADR-0021](adr/0021-product-tooling-platform-separation.md)). Runs as a container under Aspire
and in the lab. The product's LocalPay adapter is written against LocalPay's published API
document, not its code.

- **Behaves like a real provider:** create a payment → hosted payment page (simple page with
  Approve / Decline) → redirect back to us → signed webhook (HMAC); status, refund and
  payment-listing APIs; sessions expire, and can be expired early while still open.
- **Scenarios chosen by special amounts or card numbers** (like Stripe's test cards):
  - decline
  - webhook delayed by N seconds
  - webhook sent twice, or out of order
  - no webhook at all
  - timeout or HTTP 500 when we create the payment
  - payment completing at the same moment we expire the session
  - successful payment arriving after the seat hold expired
- **Provider limits on demand:** a configurable rate limit (to mimic Stripe's 25 requests/s)
  and response latencies sampled from real Stripe calls.
- **Headless mode for load tests:** approves payments automatically after a random delay,
  no browser involved.

## Stripe

The adapter (`Payments.Infrastructure/Providers/StripeProvider.cs`) uses Stripe's own library:
a payment is a Checkout Session, our order ID is its client reference and the idempotency key, and
the session is expired at the checkout deadline. It is contract-tested against `stripe-mock`
without a Stripe account ([ADR-0050](adr/0050-stripe-adapter-without-an-account.md)); the demo and
the system tests use LocalPay only.

**Turning on Stripe's test mode** (a free account, no money moves):

1. In the Stripe dashboard (test mode), allow only card and wallet payment methods: they settle at
   once ([ADR-0010](adr/0010-hold-lifetimes-and-payment-expiry.md)).
2. Give the AppHost the keys, which stay on your machine:
   ```
   dotnet user-secrets --project dev/AppHost set Stripe:SecretKey sk_test_...
   stripe listen --print-secret                      # the signing secret for forwarded webhooks
   dotnet user-secrets --project dev/AppHost set Stripe:WebhookSecret whsec_...
   ```
3. Forward Stripe's webhooks to WebhookIngress while the AppHost runs:
   ```
   stripe listen --events checkout.session.completed,checkout.session.async_payment_succeeded,checkout.session.async_payment_failed,refund.updated --forward-to http://localhost:5103/webhooks/stripe
   ```
4. Make Stripe the provider for new checkouts: `Payments__Provider=stripe` on PublicApi (LocalPay
   stays the default; the fan choosing a provider comes later).

Events Payments doesn't act on are acknowledged and ignored, so selecting more events does no harm.

## Keeping our database and the provider consistent

A database transaction and a provider call can't be one atomic operation, and no transaction
spans two modules ([ADR-0002](adr/0002-module-boundary-rules.md)). The flow is built so that
every failure can be recovered.

1. **Fan holds seats.** Inventory, one transaction: seats claimed with an expiry
   ([ADR-0011](adr/0011-seat-inventory-concurrency.md)). No order yet.
2. **Fan starts checkout.** Orders coordinates, each module in its own transaction:
   - Inventory moves the hold to `InCheckout` and pushes its expiry out to a backstop.
   - Orders creates a `Pending` order with a checkout deadline (10 min).
   - Payments records the attempt, calls the provider **outside any transaction** (order ID as
     idempotency key, session expiry 30 min, Stripe's minimum), stores the session and returns
     the checkout URL.
3. **Fan pays on the provider's hosted page** and is redirected back. The redirect alone
   doesn't prove payment; the return page polls the order status.
4. **Webhook arrives** at WebhookIngress. Verify the signature, store it in the inbox, drop
   duplicates. Payments marks the attempt `Succeeded` (amount checked) and publishes
   `PaymentSucceeded` through its outbox.
5. **Orders confirms the seats.** It calls `Inventory.ConfirmHold` (safe to repeat):
   - **Confirmed** → order `Confirmed`, `OrderConfirmed` published.
   - **Seats lost** → order `Refunding`, Payments asked to refund.
6. **A worker issues the tickets** (PDF + QR) and they are emailed.
7. **At the checkout deadline**, if unpaid: **cancel the payment at the provider first**
   (Stripe: expire the session), **then release the seats**. If the provider says the payment
   already completed, wait for its webhook instead. If the provider is unreachable, retry; the
   seats stay held at most until the backstop (~35 min).

Full flow and crash analysis: [ADR-0009](adr/0009-checkout-without-shared-transaction.md).
Hold lifetimes: [ADR-0010](adr/0010-hold-lifetimes-and-payment-expiry.md).

## Failure handling

| What goes wrong | How it's handled |
|---|---|
| Our call to create the payment times out | Retry with the same idempotency key; the provider returns the same payment |
| Webhook arrives twice | Duplicates dropped by event ID |
| Webhook arrives before the fan is redirected back | Webhook is the source of truth; the return page polls the order status |
| Webhook never arrives | Reconciliation job asks the provider about orders stuck in `Pending` |
| Crash after the provider charged the card but before our database update | Webhook retry or reconciliation completes the order |
| Payment completes after the hold expired | Prevented on the normal path: the session is expired before seats are released. If it still happens (race at the deadline), `ConfirmHold` reports the seats lost and the payment is refunded automatically |
| Provider unreachable when we cancel at the deadline | Retry; seats stay held until the backstop, after which no payment can complete |
| A payment exists at the provider but we have no order (e.g. after a point-in-time restore) | Reconciliation finds it with `ListPaymentsAsync` and refunds it |
| Event cancelled | Refunds requested per order, executed by a rate-limited worker; refund webhooks update each order |
| Chargeback | Webhook → `DisputeOpened` → Orders revokes the order → tickets invalidated |

## What this teaches

Idempotency keys, the outbox, an inbox that drops duplicates, webhook signature
verification, reconciliation, compensating actions and why their order matters, and testing
failure paths on purpose.
