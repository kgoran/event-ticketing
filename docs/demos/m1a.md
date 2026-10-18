# M1a demo runbook (about 15 minutes)

How to run the demo from [the M1a plan](../milestones/m1a.md#demo-script-about-15-minutes), step by
step. `dev/AppHost.Tests` runs the same steps as a dry run on every pull request
(`DemoDryRunTests`, `DemoHoldExpiryTests`, `DemoCheckoutDeadlineTests`), so if CI is green, the
demo works.

## Before you start

- .NET SDK (the version in `global.json`) and Docker Desktop, running.
- Port 8180 free (Keycloak) and 5101–5104 (the hosts).
- Visual Studio, Rider, or VS Code with the REST Client extension, for the `.http` files.

```
dotnet run --project dev/AppHost -- --Demo=true
```

**Demo mode** shortens the timings so expiry can be shown live: holds last **1 minute**, checkouts
**2 minutes** (normally 8 and 10). The console prints the dashboard's URL; open it.

## The steps

| # | Do | Show |
|---|---|---|
| 1 | Open the dashboard | Resources: PostgreSQL, Keycloak, LocalPay, the Migrator **Finished**, PublicApi, BackOfficeApi, WebhookIngress and Workers running, `seed` waiting to be started |
| 2 | Press **Start** on `seed` (or run `dotnet run --project tooling/seed`) | Its console log: a 2,000-seat "Demo Arena" with a standing floor, and two events on sale, "Demo Concert" and "Failure Scenarios" |
| 3 | `PublicApi.http`: *Browse*, *Search*, *Event page*, *Seat map*, *What's free* | Full-text search; the seat map under a versioned, cache-forever URL; availability per section |
| 4 | *Sign in as ana*, *Ana holds two seats* → 201; *Sign in as ben*, *Ben tries the same seats* → 409 | All or nothing, no waiting: the claim is one SQL statement (ADR-0011) |
| 5 | *Ana checks out*, open `redirectUrl` in a browser, click **Approve**; then *Ana's order* until Confirmed | Pending → Confirmed: the webhook, not the redirect, confirms it (the browser lands on the API's order URL, which needs a token: the return page is M1b's UI) |
| 6 | Dashboard → **Traces**, search `command StartCheckout` | One trace: StartCheckout → BeginCheckout → CreateOrder → CreateCheckout (and LocalPay) → `payment succeeded` → `consume payments.payment-succeeded` → ConfirmHold. The hold's and the webhook's own traces are linked (docs/observability.md) |
| 7a | *Cid holds a seat*; wait a minute; *Dan holds the same seat* | Dan gets it the moment Cid's hold expires: nothing has to sweep it first |
| 7b | *Eva checks out and walks away*; wait two minutes; *Eva's order*, and her LocalPay page | Order Expired; the LocalPay page says **expired**; the seat is free again. Payment cancelled first, seats released second (ADR-0010) |
| 8 | *Failure Scenarios*: see the table below | Each ends consistent |
| 9 | Open a pull request on GitHub | The three required checks; the dry run of this demo is one of them |

### Step 8: LocalPay's scenarios

"Failure Scenarios" is priced so that the amount picks LocalPay's scenario (its last two digits).
**Hold exactly one seat**: two seats at 25.03 make 50.06, which is no scenario.

| Section | Price | What LocalPay does | What you see |
|---|---|---|---|
| A, rows 1–10 | 25.03 | Sends the webhook **twice** | Order Confirmed once. Dashboard: two webhook requests, metric `eventticketing.payments.webhooks` with `result=duplicate` |
| B | 25.05 | Completes the payment **at the moment** the deadline expires it | Don't pay; after two minutes the order is **Confirmed**, not Expired: the cancel came back "already completed", and the webhook confirmed it |
| C | 25.01 | Declines when you click Approve | Order Failed; the seat is free at once |
| D | 25.02 | Sends the webhook 10 s late | Order Pending for 10 s, then Confirmed |
| A, rows 11–20 | 25.04 | Sends **no** webhook | Order stays Pending; at the deadline Orders asks LocalPay (a status check 2 minutes after "already completed") and confirms it |

## Afterwards

Stop the AppHost (Ctrl+C). Nothing persists: the next run starts with a fresh database and realm
(ADR-0022). To run the demo again in the same session, start `seed` again: it creates new events.

## If something goes wrong

| Symptom | Fix |
|---|---|
| A request answers 401 | Tokens last 5 minutes: run the *Sign in* request again |
| `seed` fails at once | Keycloak or an API isn't ready yet: wait until the dashboard shows them running, then start it again |
| Keycloak doesn't start | Port 8180 is taken by something else |
| A hold answers 409 `hold.active_hold_exists` | One hold per fan per event: release it (*Release Ana's hold*) or use another fan (ana, ben, cid, dan, eva, fil) |
