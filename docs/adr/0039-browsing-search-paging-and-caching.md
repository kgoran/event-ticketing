# 0039. Browsing: full-text search, paging and a versioned seat map

- **Status:** Agreed
- **Date:** 2026-10-19
- **Related:** [0007](0007-module-communication.md) · [0015](0015-performance-targets.md) · [0028](0028-gateway-and-browse-caching.md) · [0038](0038-catalog-venues-seat-maps-and-event-lifecycle.md) · design-decisions §14 (C2-a, c…f)

## Context

PublicApi serves the event list, event pages and seat maps (M1a C2). [0028](0028-gateway-and-browse-caching.md)
says seat maps live under versioned URLs and are cached for good, and that browse responses
carry correct `Cache-Control`; it didn't say how search, paging or versions work.

## Decision

- **Search** is PostgreSQL full-text search over a stored, generated `tsvector` of the event and
  venue names, with the `simple` configuration (no stemming), a GIN index and
  `websearch_to_tsquery`. Matches are ranked by `ts_rank`, then by date.
- **Paging** uses page numbers with `hasMore` and no total count; at most 50 items a page.
- **The list** shows published events that haven't started, including those not on sale yet,
  each with its sales phase. Drafts are never public (404).
- **The seat map** is served at `/events/{id}/seat-map/{version}`. The version is the first 12
  hex digits of a SHA-256 over the map's content, set at publish. The map carries the layout and
  each seat's price category, **but no prices**, so a price change keeps its URL. Unknown
  versions answer 404.
- **Cache-Control:** browse responses `public, max-age=30`; the seat map
  `public, max-age=31536000, immutable`. Output caching in Valkey arrives with M3 ([0028](0028-gateway-and-browse-caching.md)).

## Alternatives considered

| Alternative | Why not |
|---|---|
| A language-specific text-search configuration | Stemming mangles proper nouns, and names mix languages |
| `plainto_tsquery` / `to_tsquery` | The first ignores phrases and exclusions; the second fails on ordinary user input |
| Total counts in the list | A count over every match on each request costs more than it's worth |
| Keyset paging | Doesn't fit rank-ordered search results |
| Versioning the seat map by publish time | Equally stable here, but a content hash also stays right if layouts ever change after publishing |
| Prices in the seat map | Every price change would invalidate a response cached for a year |

## Consequences

- The client assembles the event page from three responses: the page (prices), the seat map
  (layout, cached for good) and Inventory's availability (fast-changing).
- Search quality is "contains these words"; fuzzy matching would need an extension or a
  search engine, which nothing requires yet.
