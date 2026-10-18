# 0018. Lab capacity comes from configuration and the environment, never from code

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0011](0011-seat-inventory-concurrency.md) · [0017](0017-scaling-lab-and-load-generator.md) · [0019](0019-autoscaling-signals.md) · [0021](0021-product-tooling-platform-separation.md)

## Context

To show autoscaling on a laptop, one instance must run out of capacity at a small, known
rate, such as 20 requests/s.

- **A delay alone doesn't do it.** `await Task.Delay(…)` holds no thread and uses no CPU, so one
  instance can keep thousands of delayed requests in flight. Throughput never stops at the
  intended rate; requests just take longer.
- **Capacity needs a limited number of work slots:** capacity per instance = slots ÷ time per
  request. Beyond that, requests queue, and when the queue is full they are rejected at once.
- **The product must not contain test or simulation code**
  ([0021](0021-product-tooling-platform-separation.md)).

## Decision

**The product ships only real features. The lab changes the product's configuration and
environment, never its code.**

```
capacity per instance = concurrency limit ÷ time per request     2 ÷ 0.1 s = 20 req/s

request ─► queue (max 4) ─► 2 work slots, each busy ~100 ms ─► response
                │ queue full
                ▼
          503 at once (load shedding)
```

- **Load shedding is a real product feature** (design-decisions §8; target T7): the ASP.NET Core
  concurrency limiter, with a limit and a short queue, rejecting the rest with 503. Workers have
  a real setting for how many messages each instance handles at once.
- **In the lab, the limits are set low** (default: 2 concurrent requests, queue of 4), and
  **Toxiproxy**, a proxy between the product and PostgreSQL, adds latency to every database
  round trip (default 100 ms, adjustable live from the lab). Result: about 20 requests/s per
  instance.
  - A hold is one database round trip ([0011](0011-seat-inventory-concurrency.md)), so for
    holds the number is exact.
  - Requests with several round trips are slower. The lab shows the capacity it actually
    measures per instance, so predictions stay possible.
- **Realistic mode:** a low CPU limit per pod (e.g. 0.1 core) instead, so instances genuinely
  run out of CPU and standard CPU-based autoscaling applies.
- **Database replicas:** real read queries, with low CPU limits on replica pods. A preset can
  switch the browse cache off through configuration to push reads to the replicas.
- **Utilization** comes from the concurrency limiter's built-in metrics (requests in progress
  and queued). No metric is added just for the lab.
- **The lab environment runs the same product images as any other environment.** Only its
  configuration (`values-lab.yaml`) differs. A permanent "lab capacity limits on" banner in the
  Scaling Lab prevents anyone mistaking these numbers for real performance; capacity targets
  are always measured with the limits off ([0015](0015-performance-targets.md)).

With the defaults (slider 0–200 requests/s, at most 8 instances, autoscaling target 70%
utilization ≈ 14 requests/s per instance), the cluster carries about 112 requests/s at target
and about 160 requests/s flat out. Above that, load shedding rejects the excess, so the top of
the slider shows the instance cap at work.

## Alternatives considered

| Alternative | Why not |
|---|---|
| A simulation middleware in the product, switched off by default | Test code in production images |
| Injecting simulation code through an extra assembly loaded only in lab images (`IHostingStartup`) | The lab image would no longer be the image we ship; only works for web hosts, not Workers; hidden mechanics are bad for teaching |
| A request-rate limit per instance (token bucket) | Rejects the excess, but there is no queue and no growing latency to watch |
| A delay only | Doesn't saturate anything, as explained above |

## Consequences

- Capacity is a little less exact than with simulation code; that is the price of a clean
  product.
- The product's load shedding is exercised constantly, which is a benefit.
- Toxiproxy sits in the lab's database path; the product's connection string points at it in
  `values-lab.yaml`.
