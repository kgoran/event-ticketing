# 0008. Seven deployables

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0001](0001-modular-monolith-with-several-hosts.md) · [0019](0019-autoscaling-signals.md) · [0021](0021-product-tooling-platform-separation.md) · design-decisions §1, §7

## Context

The draft host list had four hosts: `PublicApi`, `BackOfficeApi`, `WebhookIngress`,
`Workers`. The flows designed in the architecture pass need three more.

## Decision

| Host | Runs (modules' endpoints and consumers) | Clients | Scales on | Why it is separate |
|---|---|---|---|---|
| **PublicApi** | Catalog (read), Inventory (availability, holds), Orders (checkout, order status), Ticketing (my tickets) | fans | utilization / request rate | fan traffic |
| **WaitingRoom** | WaitingRoom | fans during an on-sale | request rate; sized for the whole spike | it protects everything else, so it must survive the spike itself; it only touches Valkey |
| **WebhookIngress** | Payments: verify the signature, store the webhook in the inbox, reply 200 | Stripe, LocalPay | small; at least two instances on different nodes | payment confirmations never wait behind fan traffic; different security setup |
| **BackOfficeApi** | Catalog (manage), Organizers, Orders and Payments (admin, refunds, reconciliation view), Reporting, CheckIn (device registration) | organizers, admins | fixed at two instances | low traffic, different authentication |
| **CheckInApi** | CheckIn: door-list download, scan upload | scanner devices | request rate at doors-open | a doors-open burst can coincide with an on-sale; devices have their own credentials |
| **Workers** | all event consumers, the outbox relay, the scheduler (expiry sweeper, checkout deadlines, reconciliation, refunds), PDF rendering, email | — | backlog (KEDA); can be split by configuration, e.g. `workers-ticketing` | background work off the request path |
| **Migrator** | database migrations for all modules | the deployment pipeline (as a Kubernetes Job) | runs once per release | several instances migrating at startup would race; additive changes run before the rollout, removals after it |

- Each host picks which modules' endpoints and consumers it runs by host kind
  (`IModule.MapEndpoints(app, hostKind)`, design-decisions §7).
- PublicApi may later run as two deployments, browse and sales, by configuration only (M5).
- Whether we build one container image per host or one image that selects the host at
  startup is decided in M2.

Not product hosts: `dev/AppHost` (Aspire, development only), LocalPay, the Scaling Lab and the
load generator (all tooling, [0021](0021-product-tooling-platform-separation.md)), the
frontends, and the identity provider if we host it ourselves.

## Alternatives considered

| Alternative | Why not |
|---|---|
| CheckIn inside BackOfficeApi | Fine until M3, but a doors-open burst and an on-sale would then compete |
| Waiting room inside PublicApi | The thing that absorbs the spike would go down with what it protects |
| Migrations at application startup | Races between instances; no control over expand/contract ordering |

## Consequences

- Seven deployables to build, deploy and monitor; each host is a thin composition root.
- The autoscaling signal for each host is in [0019](0019-autoscaling-signals.md).

## Updates

- **2026-10-18:** From M1a the Migrator also runs under Aspire, before the other hosts start
  ([0016](0016-teaching-milestones.md), update).
