# 0036. Trunk-based development with pull-request checks

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0005](0005-no-paid-services-local-first.md) · [0021](0021-product-tooling-platform-separation.md) · [0034](0034-test-stack.md)

## Context

The project teaches CI/CD. Long-lived branches cause merge pain and delay feedback, which works
against continuous delivery.

## Decision

- `main` is always releasable.
- Work happens on short-lived branches (a day or two) and is merged through pull requests.
- Every pull request runs: build, unit and integration tests, architecture tests
  ([0034](0034-test-stack.md)); container image builds join in M2.
- `product-ci` exists from the very first commit; `tooling-ci` when there is tooling;
  the nightly `system-tests` from M2 ([0021](0021-product-tooling-platform-separation.md)).
- Unfinished work is hidden behind feature flags (introduced in M2) rather than kept on long
  branches.

## Alternatives considered

| Alternative | Why not |
|---|---|
| GitFlow (long-lived develop and release branches) | Merge pain; doesn't match continuous delivery |
| Pull-request checks that are optional | The checks lose their teaching value if they can be skipped |

## Consequences

- Pull requests stay small; CI must stay fast (minutes, not tens of minutes).
- GitHub can *enforce* required checks (branch rules) on a free plan only in a public
  repository. In a free private repository the checks still run, but enforcing them is team
  discipline. Which applies depends on the repository's visibility (design-decisions §13).

## Updates

- **2026-10-18:** The repository is public:
  [github.com/kgoran/event-ticketing](https://github.com/kgoran/event-ticketing). A ruleset on
  `main` (pull request required, checks must pass) is added together with `product-ci` in M1a,
  once there are checks to require.
- **2026-10-21 (D34):** product-ci builds every container image on every pull request. A `lab`
  workflow runs `lab up` in kind with a smoke test on pull requests that touch deployment files,
  and nightly. [M2 plan](../milestones/m2.md).
