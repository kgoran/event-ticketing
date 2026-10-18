# 0011. Seats are claimed with one conditional SQL statement

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0010](0010-hold-lifetimes-and-payment-expiry.md) · [0012](0012-build-our-own-waiting-room.md) · [0015](0015-performance-targets.md) · design-decisions §4, §8, §13 (Q3)

## Context

During an on-sale thousands of fans compete for the same seats of one event. A popular event
is a single hot spot that sharding can't help: one event lives on one database writer. No
seat may ever be sold twice, and holds must stay fast under contention.

## Decision

**A hold claims its seats in one SQL statement, in one round trip, on the primary:**

```sql
-- shape only, not final code
WITH claimed AS (
  SELECT unit_id FROM inventory.units
  WHERE event_id = @event AND unit_id = ANY(@requested)
    AND (status = 'Available' OR (status = 'Held' AND held_until < now()))
  ORDER BY unit_id
  FOR UPDATE SKIP LOCKED)
UPDATE inventory.units u
SET status = 'Held', hold_id = @hold, held_until = now() + @ttl
FROM claimed
WHERE u.event_id = @event AND u.unit_id = claimed.unit_id;
-- fewer rows than requested → roll back and answer 409; otherwise insert the hold and commit
```

- Row locks last for one statement only. Losers get an immediate 409, with no waiting, no
  deadlocks (locks are taken in a fixed order, and locked rows are skipped) and no retries.
- **One row per sellable place.** Standing areas are stored as anonymous slot rows too
  (20,000 standing places = 20,000 rows), so there is no single counter row everyone fights
  over, and one code path serves both seated and standing tickets.
- "Best available in this section" is the same statement with a section filter and
  `ORDER BY rank LIMIT n`.
- All or nothing per hold. At most one active hold per fan per event (unique partial index)
  and a maximum number of seats per hold ([0024](0024-domain-scope-m1-m6.md)).
- Hand-written SQL on the hold path only. Everything else in Inventory uses EF Core with
  optimistic concurrency (`xmin`), e.g. an organizer blocking seats.
- The consistency boundary is the seat, not the event: contention decides how big an
  aggregate should be.
- No outbox message per hold, to keep the most contended transaction as small as possible.
- Second safety net: a unique index in Ticketing on valid tickets per `(event_id, seat_id)`.

**Levers for one hot event**, in the order we reach for them:

1. The waiting room's admission rate ([0012](0012-build-our-own-waiting-room.md)). You can't
   scale one row; you control how many people reach it. The rate must also respect the slowest
   downstream system: creating the payment session happens while the fan waits, and Stripe
   allows 25 requests/s per endpoint by default.
2. A sold-out flag per zone in Valkey, so we answer 409 without touching the database.
3. One availability snapshot per event, rebuilt about once a second, instead of an
   availability query per user.
4. Short transactions, PgBouncer in transaction mode.
5. Inventory on its own PostgreSQL server (possible because of schema-per-module), so an
   on-sale can't slow Orders and Payments down.
6. Only if measurements demand it: batch hold requests per instance, then option D below.

## Alternatives considered

| Approach | What happens when everyone wants the same event | Verdict |
|---|---|---|
| A. Lock first (`SELECT … FOR UPDATE`, then `UPDATE`) | Locks are held across network round trips; requests queue; deadlocks unless locks are always taken in seat order | Rejected for holds |
| B. Optimistic concurrency on an EF Core aggregate (`xmin`) | With one aggregate per event, every hold conflicts with every other; even per seat, retries multiply load at the peak | Kept for rarely contended operations |
| **C. Conditional claim in one statement** | As described above | **Chosen** |
| D. One writer per event (an actor or Kafka partition holding state in memory) | Highest throughput, but replies become asynchronous and failover and state recovery get hard; one hot event is still one CPU core | Documented next step; built only if C misses the target |
| E. Valkey as the inventory store, saved to the database later | Fast, but a Valkey failover can lose confirmed holds, which means double sales | Rejected |
| F. `SERIALIZABLE` transactions | Under contention many transactions fail and must retry | Rejected |

## Consequences

- A seat that another request is claiming at that moment counts as unavailable. If that
  claim then rolls back, our request got a 409 it didn't strictly need; the fan simply retries.
- Two code paths write seat state (the SQL above and EF Core); both need integration tests.
- Holds are one round trip, which makes the lab's capacity predictable
  ([0018](0018-capacity-from-configuration-not-code.md)).
