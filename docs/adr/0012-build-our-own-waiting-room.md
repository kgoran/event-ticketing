# 0012. Build our own waiting room

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0005](0005-no-paid-services-local-first.md) · [0008](0008-hosts.md) · [0011](0011-seat-inventory-concurrency.md) · design-decisions §13 (Q4)

## Context

On-sale traffic can reach 50–100× normal within seconds. Autoscaling takes 30–90 seconds, so
something has to hold fans back and let them in at a rate the system can serve.

Checked on 2026-10-18: Cloudflare Waiting Room needs a Business or Enterprise plan. Business
includes one basic first-come-first-served room without scheduled events; scheduled on-sales
need an Enterprise add-on. We have no budget for paid services
([0005](0005-no-paid-services-local-first.md)).

## Decision

A **WaitingRoom** host backed by Valkey, issuing signed admission tokens:

- **Fair start:** everyone who arrives before the on-sale starts joins a pre-queue. At the
  start time it is shuffled randomly, so refreshing at 09:59:59 gives no advantage. People who
  arrive later queue in arrival order.
- **Admission:** a controller lets fans through at rate R, set per on-sale. R must respect the
  slowest downstream system: Stripe's default of 25 requests/s per endpoint caps how fast
  checkout sessions can be created (LocalPay simulates this limit).
- **Polling:** fans check their position at an interval the server sets (10–30 s, with some
  randomness), so polling load stays predictable.
- **Admission token:** an admitted fan gets a signed token (event, fan, expiry ≈ 15 min).
  PublicApi verifies it locally, without calling the WaitingRoom. The token is the seam: if we
  ever buy a product, only the token issuer changes.
- **Failure:** if Valkey is lost, the on-sale pauses (fail closed); a runbook covers recovery.

## Alternatives considered

| | Build (chosen) | Cloudflare Waiting Room | Queue-it and similar |
|---|---|---|---|
| Teaching value | rate limiting, fairness, back-pressure, stateless tokens | black box | black box |
| Runs locally, in CI, under k6 | yes | no | no |
| Scheduled on-sales | we build it | Enterprise add-on only | yes |
| Cost | none | Business plan or higher | enterprise pricing |

## Consequences

- Our own servers carry the queue polling. The host is small, Valkey operations are O(1), and
  the server controls the polling interval.
- Bot protection is weaker than a commercial edge service; bots are out of scope.
- If the WaitingRoom fails, on-sales stop; this is a deliberate choice over letting everyone in.
