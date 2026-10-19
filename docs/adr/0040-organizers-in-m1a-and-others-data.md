# 0040. Organizers in M1a, and what callers learn about other people's data

- **Status:** Agreed
- **Date:** 2026-10-19
- **Related:** [0023](0023-single-tenant-organizer-scoped-data.md) · [0027](0027-identity-provider-keycloak.md) · [0033](0033-command-pipeline.md) · [M1a plan](../milestones/m1a.md) · design-decisions §14 (G1-a…c, C1-i)

## Context

Organizer membership travels in the login token ([0023](0023-single-tenant-organizer-scoped-data.md),
[0027](0027-identity-provider-keycloak.md)), and keeping Keycloak in step with the Organizers
module is M1b. M1a still needs organizers and their members on record, and a consistent answer
when someone asks about data that isn't theirs: another organizer's event, another fan's hold
or order.

## Decision

**Organizers in M1a**

- The Organizers module keeps organizers and a member list. **The token stays the authority on
  membership**: adding a member records it but grants no access until M1b.
- Admins register organizers, optionally under an existing Keycloak group's ID. That's how the
  realm's seeded organizer gets registered (the seeder, `BackOfficeApi.http`).
- An organizer's members and admins manage its member list.

**Other people's data**

- **Reads** of something the caller doesn't own answer **404**, the same as something that
  doesn't exist: an ID reveals nothing about others.
- **Commands** on it are refused by their authorizer in the pipeline with **403**
  ([0033](0033-command-pipeline.md): fail closed).
- Where a command addresses something through the caller's own scope (releasing "my" hold,
  creating an event at "my" venue), someone else's is "not found" (404) too.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Organizers keeps Keycloak in step in M1a | Planned for M1b; M1a stays on the realm import |
| Seeding the organizer from a migration | Development data in the product's schema history |
| 403 for reads as well | Confirms that the ID exists |
| 404 for every refused command | Hides authorization failures from the pipeline's audit and logs, where 403 belongs |

## Consequences

- In M1a, an organizer's members are configured in the realm file; the member list in the
  database is a record next to it.
- The rule above applies to every module; tests check it per endpoint.
