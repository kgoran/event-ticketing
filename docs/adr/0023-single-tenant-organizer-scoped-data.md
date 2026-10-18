# 0023. Single-tenant platform; organizers own their data

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0006](0006-ticketing-modules.md) · [0007](0007-module-communication.md) · design-decisions §5, §13 (Q7)

## Context

Organizers create events and must see only their own events, sales and staff. The question
was whether organizers are *tenants* (isolated data, per-tenant configuration) or simply
*owners* of data on one shared platform.

## Decision

- One platform, single-tenant.
- Data that belongs to an organizer carries an `organizer_id`.
- Authorization checks organizer membership, which travels in the login token as claims; no
  runtime call to the Organizers module is needed.
- No tenant isolation: no row-level security, no per-tenant configuration, no per-tenant
  databases.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Organizers as tenants (tenant ID everywhere, row-level security, tenant context in every request) | Adds a whole concern the brief doesn't need; ticketing's hard problems are elsewhere |

## Consequences

- The `tenant/context` step in the command pipeline (design-decisions §5) carries the
  organizer context.
- Changing to real multi-tenancy later means adding tenant IDs and row-level security
  everywhere, which is expensive. The Organizers module would be its home.
