# M1a demo runbook (about 15 minutes)

How to run the demo from [the M1a plan](../milestones/m1a.md#demo-script-about-15-minutes), step by
step. `dev/AppHost.Tests` runs the same steps as a dry run on every pull request
(`DemoDryRunTests`, `DemoHoldExpiryTests`, `DemoCheckoutDeadlineTests`), so if CI is green, the
demo works.

## Before you start

- .NET SDK (the version in `global.json`) and Docker Desktop, running.
- Port 8180 free (Keycloak), 5100 (the fan site) and 5101–5104 (the hosts).
- Two browser windows that don't share cookies, one per fan: a normal and a private window, or
  two browsers.
- Optional, for showing the API itself: Visual Studio, Rider, or VS Code with the REST Client
  extension, for [`PublicApi.http`](../../product/src/Hosts/PublicApi/PublicApi.http).

```
dotnet run --project dev/AppHost -- --Demo=true
```

**Demo mode** shortens the timings so expiry can be shown live: holds last **1 minute**, checkouts
**2 minutes** (normally 8 and 10). The console prints the dashboard's URL; open it. The fan site
is at **http://localhost:5100**. Fans: ana, ben, cid, dan, eva, fil; each password is the name.

## The steps

| # | Do | Show |
|---|---|---|
| 1 | Open the dashboard | Resources: PostgreSQL, Keycloak, LocalPay, the Migrator **Finished**, PublicApi, BackOfficeApi, WebhookIngress, Workers and the fan site running, `seed` waiting to be started |
| 2 | Press **Start** on `seed` (or run `dotnet run --project tooling/seed`) | Its console log: a 2,000-seat "Demo Arena" with a standing floor, and two events on sale, "Demo Concert" and "Failure Scenarios" |
| 3 | Fan site: the event list, a search ("concert"), then **Demo Concert** | Full-text search; the seat map (geometry cached for good under a versioned URL) coloured by price category, with what's free now |
| 4 | Window 1: **Sign in** as ana and click two free seats. Window 2: sign in as ben and click the **same** two. Ana presses **Hold 2 seats**, then Ben does | Ana gets them with a 1-minute countdown; Ben is refused at once (409, all or nothing: the claim is one SQL statement, ADR-0011), and the seats turn grey on his map |
| 5 | Ana: **Check out**, then **Approve** on LocalPay's page | Back on the fan site's order page: *Waiting for your payment*, then *confirmed*. The webhook confirms it, not the redirect: the page asks PublicApi until the order settles |
| 6 | Dashboard → **Traces**, search `command StartCheckout` | One trace: StartCheckout → BeginCheckout → CreateOrder → CreateCheckout (and LocalPay) → `payment succeeded` → `consume payments.payment-succeeded` → ConfirmHold. The hold's and the webhook's own traces are linked (docs/observability.md) |
| 7a | Cid holds a seat and walks away; wait for his countdown; Dan holds the same seat | Dan gets it the moment Cid's hold expires: nothing has to sweep it first. Cid's page says the hold expired |
| 7b | Eva holds a seat and checks out, then presses **Back to FanSite** on LocalPay's page without paying; wait two minutes on her order page | The order page turns to *The time to pay ran out*; its *Continue to payment* link, opened before that, now shows LocalPay's page **expired**; the seat is free again. Payment cancelled first, seats released second (ADR-0010) |
| 8 | **Failure Scenarios**: see the table below | Each ends consistent |
| 9 | Open a pull request on GitHub | The required checks; the dry run of this demo is one of them |

The same steps through the API alone are the requests in
[`PublicApi.http`](../../product/src/Hosts/PublicApi/PublicApi.http), top to bottom: each uses
values from the ones before, so start at *Step 3*.

### Step 8: LocalPay's scenarios

"Failure Scenarios" is priced so that the amount picks LocalPay's scenario (its last two digits).
**Hold exactly one seat**: two seats at 25.03 make 50.06, which is no scenario. The legend on the
seat map names each price category after its scenario; "Best available" with a quantity of 1 is
the quickest way.

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
| The dashboard doesn't start: "The specified network password is not correct" | `ASPNETCORE_Kestrel__Certificates__Default__*` environment variables point every ASP.NET Core process at a certificate. Remove them (or set them per project in `launchSettings.json`), then restart the terminal and the editor |
| The fan site shows you signed out | It restarted: the tokens live in its memory (ADR-0049). Sign in again; Keycloak usually remembers you |
| A `.http` request answers 401 | Tokens last 5 minutes: run the *Sign in* request again (the fan site refreshes its own) |
| `seed` fails at once | Keycloak or an API isn't ready yet: wait until the dashboard shows them running, then start it again |
| Keycloak doesn't start | Port 8180 is taken by something else |
| "You already hold seats for this event" | One hold per fan per event: release it, or use another fan (ana, ben, cid, dan, eva, fil). A hold made in another browser expires within a minute in demo mode |
