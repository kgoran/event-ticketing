# 0026. Module project layout: four projects for rich modules, two for thin ones

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0002](0002-module-boundary-rules.md) · [0006](0006-ticketing-modules.md) · [0021](0021-product-tooling-platform-separation.md) · design-decisions §3, §13 (Q1)

## Context

Two layouts were on the table (design-decisions §3):

- **Option A, Clean Architecture per module:** `X.Domain`, `X.Application`,
  `X.Infrastructure`, `X.Contracts`. The compiler enforces layering; the team already knows it;
  about four projects per module, and a feature touches two or three of them.
- **Option B, two projects per module:** `X.Contracts` plus `X`, with layers as folders.
  Fewer projects; layering enforced only by architecture tests.

Module boundaries are identical in both. With ten modules
([0006](0006-ticketing-modules.md)), Option A everywhere means about 40 module projects, many of
them for modules that are little more than CRUD.

## Decision

Use the layout that matches the module's style ([0006](0006-ticketing-modules.md)):

| Modules | Layout | Projects |
|---|---|---|
| Inventory, Orders, Payments, Ticketing, CheckIn (full domain model) | Option A | `X.Domain`, `X.Application`, `X.Infrastructure`, `X.Contracts` |
| Catalog, Organizers, Notifications, Reporting, WaitingRoom (thin slices) | Option B | `X.Contracts`, `X` (feature folders) |

That is 30 module projects, plus four shared projects (`Shared.Domain`, `Shared.Application`,
`Shared.Infrastructure`, `Shared.Contracts`) and the hosts.

Reference rules for Option A modules:

| Project | May reference |
|---|---|
| `X.Domain` | `Shared.Domain` |
| `X.Application` | `X.Domain`, `Shared.Application`, other modules' `Y.Contracts` |
| `X.Infrastructure` | `X.Application`, `Shared.Infrastructure` |
| `X.Contracts` | `Shared.Contracts` |
| Hosts | each module's `X.Infrastructure` (or `X` for Option B modules), for registration |

Forbidden everywhere: referencing another module's anything except its `Contracts`; any
`Shared.*` project referencing a module.

Guardrails for the Shared projects (the main risk is that they become a dumping ground):

- In: technical building blocks and value objects that mean the same everywhere (`Money`,
  `CurrencyCode`, `CountryCode`, typed-ID base, `Result`).
- Out: anything belonging to one business area. If two modules need it, the owning module
  publishes it in its `Contracts`.
- No infrastructure packages (EF Core, ASP.NET Core) in `Shared.Domain` or `Shared.Application`.
- Changes to `product/src/Shared/` need an architecture review (`CODEOWNERS`).

**Promotion rule:** a thin module that grows a real domain model moves to Option A. Because the
boundaries are identical, that is a mechanical split of one project into three.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Option A everywhere | About 40 module projects; ceremony for modules that are mostly CRUD |
| Option B everywhere | Layering would rest on tests alone in exactly the modules where it matters most |

## Consequences

- Two module shapes in one solution. That's deliberate and is a lesson in itself: layering as
  separate projects pays off where the domain is rich.
- Architecture tests cover both shapes: project references for Option A, namespaces and
  folders for Option B.
