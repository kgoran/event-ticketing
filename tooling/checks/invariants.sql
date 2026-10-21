-- T2's invariants (ADR-0015, D58): what must hold across modules once traffic has settled.
-- Each rule returns the rows that break it, as "rule | detail"; no rows means every rule holds.
-- Run by `dotnet run lab.cs -- check --invariants`, in one read-only transaction on a replica, so
-- every schema is read at the same moment and nothing can be written. The checker reads across the
-- modules' schemas, which the product itself never does (ADR-0002); it exists only in the lab.
-- Work that finishes asynchronously (tickets, refunds) gets a few minutes before it counts.

-- No seat sold twice: two confirmed orders for one place.
SELECT 'a seat sold twice' AS rule,
       format('event %s, place %s: orders %s', o.event_id, l.seat_id, string_agg(o.id::text, ', ')) AS detail
FROM orders.order_lines l
JOIN orders.orders o ON o.id = l.order_id AND o.status = 'Confirmed'
GROUP BY o.event_id, l.seat_id
HAVING count(*) > 1

UNION ALL
-- ...or two tickets for one place.
SELECT 'two tickets for one place',
       format('event %s, place %s: tickets %s', event_id, seat_id, string_agg(id::text, ', '))
FROM ticketing.tickets
GROUP BY event_id, seat_id
HAVING count(*) > 1

UNION ALL
-- No ticket without a confirmed seat: its order is confirmed, and Inventory sold the place to it.
SELECT 'a ticket without a confirmed seat',
       format('ticket %s (order %s, place %s): order %s, place %s',
              t.id, t.order_id, t.seat_id, coalesce(o.status, 'missing'), coalesce(s.status || ' to ' || coalesce(s.order_id::text, 'nobody'), 'missing'))
FROM ticketing.tickets t
LEFT JOIN orders.orders o ON o.id = t.order_id
LEFT JOIN inventory.seats s ON s.event_id = t.event_id AND s.seat_id = t.seat_id
WHERE o.status IS DISTINCT FROM 'Confirmed'
   OR s.status IS DISTINCT FROM 'Sold'
   OR s.order_id IS DISTINCT FROM t.order_id

UNION ALL
-- A confirmed order's places are sold to it in Inventory...
SELECT 'a confirmed order whose place isn''t sold to it',
       format('order %s, place %s: %s', o.id, l.seat_id, coalesce(s.status || ' to ' || coalesce(s.order_id::text, 'nobody'), 'missing'))
FROM orders.orders o
JOIN orders.order_lines l ON l.order_id = o.id
LEFT JOIN inventory.seats s ON s.event_id = o.event_id AND s.seat_id = l.seat_id
WHERE o.status = 'Confirmed'
  AND (s.status IS DISTINCT FROM 'Sold' OR s.order_id IS DISTINCT FROM o.id)

UNION ALL
-- ...and a sold place belongs to a confirmed order.
SELECT 'a sold place without a confirmed order',
       format('event %s, place %s: order %s', s.event_id, s.seat_id, coalesce(o.id::text || ' ' || o.status, 'missing'))
FROM inventory.seats s
LEFT JOIN orders.orders o ON o.id = s.order_id
WHERE s.status = 'Sold'
  AND o.status IS DISTINCT FROM 'Confirmed'

UNION ALL
-- A place held for the future has a current hold.
SELECT 'a place held without a current hold',
       format('event %s, place %s: hold %s', s.event_id, s.seat_id, coalesce(h.id::text || ' ' || h.status, 'missing'))
FROM inventory.seats s
LEFT JOIN inventory.holds h ON h.id = s.hold_id
WHERE s.status = 'Held'
  AND s.held_until > now()
  AND h.status IS DISTINCT FROM 'Active'
  AND h.status IS DISTINCT FROM 'InCheckout'

UNION ALL
-- Every paid order ends Confirmed or Refunded.
SELECT 'a paid order neither confirmed nor refunded',
       format('order %s: %s, paid at %s', o.id, coalesce(o.status, 'missing'), p.completed_at)
FROM payments.payment_attempts p
LEFT JOIN orders.orders o ON o.id = p.order_id
WHERE p.status = 'Succeeded'
  AND p.completed_at < now() - interval '5 minutes'
  AND o.status IS DISTINCT FROM 'Confirmed'
  AND o.status IS DISTINCT FROM 'Refunded'

UNION ALL
-- No refund paid twice, and never more than was paid.
SELECT 'a refund paid twice',
       format('order %s: %s refunds, %s refunded of %s paid', r.order_id, count(*), sum(r.amount_minor_units),
              (SELECT coalesce(sum(p.amount_minor_units), 0) FROM payments.payment_attempts p WHERE p.order_id = r.order_id AND p.status = 'Succeeded'))
FROM payments.refunds r
WHERE r.status = 'Succeeded'
GROUP BY r.order_id
HAVING count(*) > 1
    OR sum(r.amount_minor_units) > (SELECT coalesce(sum(p.amount_minor_units), 0) FROM payments.payment_attempts p WHERE p.order_id = r.order_id AND p.status = 'Succeeded')

UNION ALL
-- A confirmed order gets its tickets, one per place (T6 allows two minutes).
SELECT 'a confirmed order without its tickets',
       format('order %s: %s places, %s tickets, confirmed at %s', o.id, lines.places, coalesce(issued.tickets, 0), o.closed_at)
FROM orders.orders o
JOIN (SELECT order_id, count(*) AS places FROM orders.order_lines GROUP BY order_id) lines ON lines.order_id = o.id
LEFT JOIN (SELECT order_id, count(*) AS tickets FROM ticketing.tickets GROUP BY order_id) issued ON issued.order_id = o.id
WHERE o.status = 'Confirmed'
  AND o.closed_at < now() - interval '5 minutes'
  AND coalesce(issued.tickets, 0) <> lines.places;
