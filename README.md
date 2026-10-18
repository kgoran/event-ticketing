# EventTicketing

A teaching and reference project for a .NET team. The domain is event ticketing: organizers
create events, fans hold seats and pay, tickets are scanned at the door. The real goal is the
engineering that most mid-size systems run into: CI/CD, high availability, database
replication, horizontal scaling, external integrations, explicit performance targets and
zero-downtime deployment.

**Status:** design phase. The architecture is agreed; there is no application code yet.

| Start here | |
|---|---|
| [Project brief](docs/project-brief.md) | Purpose, teaching goals, domain scope |
| [Architecture overview](docs/architecture/overview.md) | Modules, hosts, key flows, performance targets, milestones |
| [Decision records](docs/adr/README.md) | One ADR per agreed decision, with the alternatives considered |
| [Decision log](docs/design-decisions.md) | Status of every decision |
| [Payments](docs/payments.md) | Stripe test mode and the LocalPay simulator; consistency and failure handling |
| [M1a plan](docs/milestones/m1a.md) | The first milestone: scope, backlog, definition of done, demo script |

## Run it locally

You need the .NET 10 SDK and Docker Desktop.

```powershell
dotnet run --project dev/AppHost                     # starts everything; open the dashboard link it prints
dotnet test --solution product/EventTicketing.slnx   # unit and architecture tests
dotnet test --project dev/AppHost.Tests              # starts the whole system and checks it is healthy
```

`dev/EventTicketing.Dev.slnx` opens product, tooling and the AppHost together in an IDE.

Everything runs locally with free tools; no paid services are used. This is not a product:
no real money and no production use.
