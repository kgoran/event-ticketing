# 0015. Performance targets T1–T11 and how they are verified

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0005](0005-no-paid-services-local-first.md) · [0016](0016-teaching-milestones.md) · [0017](0017-scaling-lab-and-load-generator.md) · [0018](0018-capacity-from-configuration-not-code.md) · project-brief "Example performance targets"

## Context

The brief gave three example targets "to be confirmed": hold a seat in under 200 ms for 99% of
requests at 5,000 requests/s; zero seats sold twice; expired holds released within 10 s.

A target can only be tested once its traffic, data and environment are defined. There is no
cloud environment ([0005](0005-no-paid-services-local-first.md)), so nothing can be measured
at 5,000 requests/s.

## Decision

**Test conditions**

- **Data:** the demo data set is small (one venue with about 2,000 seats and a few events).
  The load-test data set is a 50,000-capacity venue: 30,000 seats plus 20,000 standing places.
- **Traffic model:** an on-sale goes from zero to peak in 10 s, and 70% of hold attempts go
  for the best 20% of seats.
- **Reference machine:** the project owner's laptop, where demos run. Capacity targets are
  measured there with lab capacity limits switched off
  ([0018](0018-capacity-from-configuration-not-code.md)). CI keeps its own baseline on its own
  runners and fails only when a change is clearly worse; the tolerance is set in M5, because
  shared runners are noisy.

**Two kinds of target**

- **Correctness targets** don't depend on machine size; they are checked at their full values.
- **Capacity targets** get their numbers from one baseline run in M5. After that, a change
  that makes them worse fails the build. The brief's 5,000 requests/s and 100,000 queued fans
  stay only as the reasoning behind the design.

| # | Kind | Target | How it is checked |
|---|---|---|---|
| T1 | capacity | Hold requests: 99% answered within 200 ms, under 0.1% errors (a 409 "seat taken" is not an error), at the baseline rate. Design aim: 5,000 attempts/s platform-wide, 2,000/s on one event | k6 with a ramping arrival rate, threshold `http_req_duration{op:hold} p(99)<200`; cross-checked with server-side OpenTelemetry metrics |
| T2 | correctness | No seat sold twice; no ticket without a confirmed seat; every paid order ends Confirmed or Refunded; no refund paid twice; once traffic stops, seat states add up to capacity | Invariant checker ([0021](0021-product-tooling-platform-separation.md)) after every load test and lab preset; CI integration tests with parallel holds |
| T3 | correctness | An expired seat can be claimed the instant it expires; it shows as available within 10 s (99%); abandoned checkouts are released within 60 s of their deadline | k6 users that hold and never pay; probes measure time from expiry to a successful new hold and to the seat appearing in the snapshot |
| T4 | capacity | Browsing: our servers answer 99% within 100 ms at the expected cache-miss rate; the primary database's CPU doesn't rise while people browse | k6 against the origin (behind the caching layer); a test checks cache headers |
| T5 | capacity | Waiting room: 99% of joins within 300 ms at the baseline queue size; admissions within ±5% of the configured rate. Design aim: 100,000 queued, 20,000 joins/s | k6 join-and-poll scenario |
| T6 | correctness | From webhook to tickets issued and email accepted: 95% within 30 s, 99% within 2 min at the baseline payment rate; webhook endpoint answers 99% within 200 ms; no webhook lost | LocalPay headless; OpenTelemetry measures webhook received → tickets issued; backlog panel; Mailpit message count |
| T7 | capacity | At 3× the T1 baseline rate: excess rejected within 50 ms (99%); admitted requests still meet T1; normal again within 60 s after the load drops | k6 ramps past capacity, with separate thresholds for rejected and admitted requests |
| T8 | correctness | Scan decision within 300 ms offline; door list for 80,000 tickets under 5 MB; 200 devices syncing every 5 s get 99% of answers within 500 ms; a revocation reaches online devices within 30 s | k6 device simulator plus Toxiproxy (latency, packet loss, offline periods); on-device timing measured in a UI test |
| T9 | correctness | Cancelling an event with 50,000 orders (load-test data set): no refund paid twice, every order reaches a final state, speed limited only by the provider's rate limit | LocalPay with a simulated rate limit; T2 checker |
| T10 | correctness | Under load: a rolling deploy causes under 0.1% errors; a primary database failover causes under 60 s of errors and breaks none of the T2 invariants | k6 at constant load during a rollout (M2) and while killing the primary (M3) |
| T11 | correctness | After a load step that needs about 3× the instances (e.g. 50 → 150 requests/s at the lab's 50 requests/s per instance, since M3; 28 → 84 on M2's CPU signal), the new instances are ready within 60 s (demo-speed profile), with under 5% of requests rejected during the gap; scaling back in causes zero failed requests | k6 step scenario plus Kubernetes event timestamps |

**Results are the only thing kept.** k6 summaries are saved as files: baselines are committed
under `tooling/load-tests/results/`, CI runs keep theirs as build artifacts
([0022](0022-disposable-lab-environment.md)).

## Alternatives considered

| Alternative | Why not |
|---|---|
| Keep the brief's absolute numbers | Can't be measured without a cloud environment; a target nobody can test is decoration |
| Only correctness targets, no capacity targets | Loses the "explicit targets, load tests that verify them" teaching goal |

## Consequences

- Capacity numbers are laptop-sized and only comparable on the same machine.
- T1, T4, T5 and T7 get their numbers in M5; until then they have a shape but no value.
