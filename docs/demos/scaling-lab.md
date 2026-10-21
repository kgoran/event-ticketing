# Scaling Lab runbook (one presenter, one laptop)

How to put load on the system and watch what it does with it. The lab's design is
[ADR-0017](../adr/0017-scaling-lab-and-load-generator.md); what exists today is its M2 basics,
brought forward under Aspire (D20). `ScalingLabTests` in `dev/AppHost.Tests` runs the generator
against the whole system on every pull request.

## Start

```
dotnet run --project dev/AppHost
```

1. In the dashboard, press **Start** on `seed` (once per run): it puts two events on sale.
2. Open the lab at **http://localhost:5300**. It picks the first event on sale; the **Event**
   list chooses another.
3. Move the **Rate** slider. The generator sends that many requests per second to PublicApi until
   you press **Stop** or move the slider back to 0.

Nothing persists between AppHost runs (ADR-0022), so a stress test can't damage anything: stop the
AppHost and the next run starts clean.

## What you control

| Control | What it does |
|---|---|
| **Rate** | Requests per second, 0–400 on the slider. Requests go out on schedule whether or not earlier answers came back (an open model), so overload shows instead of hiding |
| **Holds** | The share of requests that hold one place (best available) and let go of it at once; the rest browse: the event list, the event page, availability. Nothing is ever sold |
| **Event** | Which event to load |
| **Holds go for** | Any section, or one section only: a hot section where the lab fans compete for the same places |
| **Scenarios** | *Steady 20/s*; *Step 28 → 84* (the step T11 measures in M5); *On-sale spike* (20, up to 200 within 10 s, 30 s there, back to 20); *Past capacity* (400 for 20 s, then back to 20). Moving the slider or **Stop** ends a scenario |

Holds are placed by two hundred lab fans, `lab01` to `lab200` (each password is the name). Each has
at most one hold at a time, as every fan does, so they manage about 200 ÷ (hold time + release
time) holds a second: over 1,000 while the system is fast, far fewer once it slows down.

## What you see

Every control, tile and chart has an **i** button: it opens a longer explanation of what that part
shows, how it is measured and how to read it.

| Tile or chart | Meaning |
|---|---|
| **Offered** | What the slider asks for. *Skipped* below it (a warning) means the generator held back: 500 requests already unanswered, or every lab fan busy with a hold. Then the generator is the limit, not the system. What was *sent* is Offered minus skipped |
| **Achieved** | Scheduled requests answered, whatever the answer. Falling behind what was sent (a warning) means the system can't keep up. Skipped requests with normal latency mean the generator is the limit; with latency climbing, they are a symptom of the system's slowness (the fans stay busy longer) |
| **OK / Refused / Failed** | Every HTTP answer, releases included. *Refused* is a 409 (the place was just taken), a 429, or a 503 with Retry-After (new holds paused by the `PauseHolds` flag): the system saying no on purpose, not an error. *Failed* is a 5xx, a timeout (5 s) or a broken connection; a 4xx other than those means a bug in the generator |
| **Hold p99 / Browse p99** | The slowest 1% of answered holds and page reads in the last second, against the targets: holds under 200 ms (T1), browsing under 100 ms (T4) |
| **In flight** | Sent and not answered yet. It climbs when the system queues |
| Charts | The last 2 minutes. Hover for a second's values; *The last 10 seconds as a table* lists them |
| **Free places** | Availability of the event under load, per section |

Every request carries trace context, and 1 in 20 is traced: in the dashboard, **Traces** shows a
sample of the load next to the fan site's requests, which are always traced
([observability](../observability.md#under-load-sampling)). **Metrics** counts every request.

## A first stress test

1. **Steady 20/s**, Holds at 20%: Achieved matches Offered, Failed is 0, latency is flat.
2. Drag the slider up in steps, waiting about 20 s at each, and watch for the first of: Achieved
   falling behind, In flight climbing, p99 crossing its target, Failed above 0. That is this
   machine's capacity for this mix: one instance of each host, no cache.
3. **On-sale spike**: the jump arrives faster than any autoscaler could react. In M5 the same
   preset shows instances being added, and the waiting room in front of them.
4. **Past capacity**: answers fall behind, In flight reaches the generator's 500, p99 reaches the
   5 s timeout and requests fail. Then watch the way back at 20/s: how long until Failed is 0
   and latency is flat again? T7 asks for 60 s.

What the owner's machine showed on 2026-10-20 (default mix, one instance of each host): at 200/s
everything was answered, holds p99 about 40 ms, browsing about 30 ms. At 400/s only about 205–250/s
were answered, the rest queued up to the 5 s timeout, and about 3% failed. *Past capacity* was
back to normal within seconds of dropping to 20/s. A harsher run (up to 1,000/s for 15 s) was
not: PublicApi stayed unhealthy for 1–2 minutes, working through requests whose callers had
already given up while new database connections timed out. That misses T7, and it is the problem
M5's capacity settings are for: reject the excess at once instead of queueing it
([0018](../adr/0018-capacity-from-configuration-not-code.md)). Until then, keep a live demo at or
below 400/s, and run *Past capacity* last.

After an overload a few places stay held: their releases timed out, so the holds expire like any
other, after 8 minutes (1 minute in demo mode). Until then those lab fans get 409s (one hold per
fan): correct, and counted as refusals.
5. **Holds go for** one section with Holds at 100%: more refusals (fans racing for the same
   places) and still no failures. Inventory never gives one place to two fans at once; the T2
   concurrency test proves it in CI.

Then press **Stop**.

## In the lab cluster: instances (M2)

Run the same lab in the cluster with `dotnet run lab.cs -- up` ([the lab](../lab.md)), same
address. The **Instances** panel then shows every host of the product with its instances, wanted
and ready: one chip per instance, ✓ ready, ▲ starting, ✕ stopping. **−** and **+** change
PublicApi's count, 1 to 8, as an operator would. **Auto** hands the same number to KEDA's
autoscaler ([ADR-0054](../adr/0054-publicapi-autoscaling-switch-in-the-lab.md)); the line under the
instances shows what it measures and wants.

1. **Steady 20/s** (or 100/s), then **+** twice: two instances start, become ready within seconds,
   and Traefik spreads the requests over all of them. No request fails.
2. **−** back down: instances stop after a 5-second grace, still without failures.
3. On one laptop more instances don't add much capacity: they share the same CPU and database.
   What they show is how scaling works, and what it needs (probe timeouts, a graceful stop, a
   connection budget: [what the lab taught](../lab.md#three-things-the-lab-taught-on-its-first-day)).
4. **Auto** on, at rest: within a minute the autoscaler gives instances back, down to one.
5. **Step 28 → 84**: at 28/s one or two instances are enough; after the step the line shows CPU
   climbing past its 70% target and the count it wants, and new instances start. Count how long
   the step takes to be served by enough ready instances: T11 asks for 60 s. No request fails.
6. **Stop**: after 30 s below target (demo speed) the instances go again; **Realistic** waits 5
   minutes. Switch Auto off and − / + work again, from the count the autoscaler left.
7. Try the same with **Holds** at 90%: holds wait on the database more than they use PublicApi's
   CPU, so latency can climb before CPU does. That is why CPU is only the stand-in until M3.

## If something is off

| Symptom | Cause |
|---|---|
| "The load generator doesn't answer" | The AppHost isn't running, or `loadgenerator` is still starting: see the dashboard |
| "Nothing on sale yet" | Start `seed` in the dashboard |
| Failed rises at a low rate | The dashboard's logs for `publicapi`; the trace of a failed request (filter by status) |
| Skipped rises before anything else | The generator held back: all 200 fans busy, or 500 requests in flight. With latency normal it is the generator's limit; with latency climbing, the system's slowness is the cause |
