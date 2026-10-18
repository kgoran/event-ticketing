# 0027. Keycloak as the identity provider

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0005](0005-no-paid-services-local-first.md) · [0007](0007-module-communication.md) · [0013](0013-offline-first-check-in.md) · [0023](0023-single-tenant-organizer-scoped-data.md) · [0024](0024-domain-scope-m1-m6.md) · design-decisions §9, §13 (Q9)

## Context

- Fans need an account to buy ([0024](0024-domain-scope-m1-m6.md)).
- Organizers and their staff, admins and door staff also sign in.
- Organizer membership must travel in the login token, so no request has to call the
  Organizers module ([0007](0007-module-communication.md),
  [0023](0023-single-tenant-organizer-scoped-data.md)).
- It must be free and self-hosted ([0005](0005-no-paid-services-local-first.md)).

## Decision

- **Keycloak** (Apache 2.0) is the identity provider for people: fans (with
  self-registration), organizers and their staff, admins and door staff.
- **The product uses only standard OIDC.** The fan UI and the back office sign in with the
  authorization code flow with PKCE. The APIs validate JWT access tokens (issuer, audience, and
  signature via the provider's published keys). Apart from configuration, there is no
  Keycloak-specific code in the product.
- **Roles and claims:** `fan`, `admin`, `door-staff`, and organizer membership as a list of
  organizer IDs with a role for each.
- **Organizers stays the source of truth for membership.** It keeps Keycloak in step through
  Keycloak's admin API, an outbound integration behind an adapter, like a payment provider.
  Access tokens are short-lived (about 5 minutes), so a membership change takes effect at the
  next token refresh.
- **Scanner devices** ([0013](0013-offline-first-check-in.md)) are settled in M3 when CheckIn is
  built. The lean is one Keycloak client per registered device (client credentials), created
  and revoked through the same adapter.
- **Where it runs:** under Aspire as a container for development. In the lab it is a platform
  component whose realm (clients, roles, test users) is imported from the repository at every
  start; because the lab is disposable ([0022](0022-disposable-lab-environment.md)), Keycloak
  needs no database of its own there.
- **Tests:** integration tests use tokens signed with a local test key; system tests in the lab
  and in CI use the real Keycloak.

## Alternatives considered

| Alternative | Why not |
|---|---|
| ASP.NET Core Identity + OpenIddict (our own authorization server) | All .NET, and it teaches the protocol from the inside, but the team would write security-critical code while learning, plus an extra host and login UI to maintain |
| Duende IdentityServer | Commercial licence |
| ASP.NET Core Identity API endpoints without OIDC | Not standard OIDC; nothing transfers to enterprise identity providers |
| Entra ID for staff | A cloud tenant to manage; not needed. Keycloak can broker it later if wanted |

## Consequences

- Keycloak adds roughly 0.5–1 GB of memory to the lab.
- Because the product only speaks OIDC, moving to Entra ID, Okta or another provider later is a
  configuration change plus the membership adapter.
- Membership changes are visible after a token refresh, not instantly.

## Updates

- **2026-10-18:** Organizers keeping Keycloak in step arrives in M1b. In M1a, organizer
  memberships are seeded in the imported realm ([M1a plan](../milestones/m1a.md)).
