# EventTicketing

A **teaching / reference project** for a .NET team. The domain is event ticketing, but the
real goal is to demonstrate the concerns most mid-size and enterprise systems run into:
CI/CD, high availability, replication, horizontal scaling, external integrations,
performance requirements and deployment requirements.

It is not a product. Keep the domain simple and the engineering concerns rich.

## Current status

Design complete (architecture agreed 2026-10-18: `docs/architecture/overview.md`, ADRs in
`docs/adr/`). Repository: https://github.com/kgoran/event-ticketing (public).
M1a is planned in `docs/milestones/m1a.md`. Next step: M1a kickoff (agree the time-box), then
the walking skeleton (backlog items F1–F3). Application code starts there.

## Working agreements

- Challenge requirements, point out risks and missing requirements, suggest better options.
- For each decision: give a recommendation with the reasoning and the alternatives considered.
- Ask instead of assuming on decisions that belong to the project owner
  (scope, cloud/budget, team, timeline).
- Record agreed decisions as ADRs in `docs/adr/` (one file per decision, `NNNN-title.md`).
  Until then, `docs/design-decisions.md` is the decision log, with a status on each item.
- Prefer patterns that transfer to other projects over domain-specific cleverness.
- No paid services; everything must run locally (ADR-0005).
- Keep product and tooling separate: nothing under `product/` references `tooling/`, and the
  product contains no test or simulation code (ADR-0018, ADR-0021).

## Docs

| File | Contents |
|---|---|
| `docs/project-brief.md` | Purpose, teaching goals, domain scope, integrations, example targets, milestones |
| `docs/design-decisions.md` | Architecture and stack decisions carried over so far, with status (Agreed / Proposed / Open) |
| `docs/payments.md` | Payment design: Stripe + LocalPay simulator, consistency flow, failure handling |
| `docs/architecture/overview.md` | Agreed architecture: modules, hosts, key flows, targets, milestones, repository, Scaling Lab |
| `docs/adr/` | Architecture decision records, one per agreed decision (index in `README.md`) |
| `docs/milestones/` | Milestone plans: scope, backlog, definition of done, demo script |

## Background

The architecture ideas here came from an earlier design discussion for an online casino
platform (seamless wallet, provider callbacks, PSPs). That domain was dropped as too
specific (jurisdictions, many integration types); its general patterns were kept.
