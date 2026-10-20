# M1b demo runbook (about 15 minutes, one presenter, one laptop)

How to run the demo from [the M1b plan](../milestones/m1b.md#demo-script-about-15-minutes), step by
step. `dev/AppHost.Tests` runs the same steps on every pull request (`DemoM1bDryRunTests`, with the
M1a dry run), so if CI is green, the demo works. Everything runs on the laptop: no internet is
needed during the demo (D19).

## The day before (online)

1. Pull the latest `main`, then run the demo once end to end:
   ```
   dotnet run --project dev/AppHost -- --Demo=true
   ```
   This builds everything and pulls the container images (PostgreSQL, Keycloak, Mailpit), so the
   next run needs no network. Stop it with Ctrl+C.
2. Optional: `dotnet test --project dev/AppHost.Tests` runs both dry runs on your machine (about 5
   minutes, Docker running).
3. Check that ports 8025 (Mailpit), 8180 (Keycloak), 5100 (fan site) and 5101–5104 are free.

## On the day

```
dotnet run --project dev/AppHost -- --Demo=true
```

Open, in this order: the dashboard (its link is in the console), the fan site at
http://localhost:5100 in a normal window **and** a private window, and Mailpit at
http://localhost:8025. Fans: ana, ben, cid, dan, eva, fil; each password is the name.

| # | Do | Show |
|---|---|---|
| 1 | The dashboard | Resources: everything from M1a, plus Mailpit. Workers now also runs Ticketing and Notifications |
| 2 | Press **Start** on `seed` | Two events on sale, as in M1a |
| 3 | Fan site, normal window: sign in as ana, **Demo Concert**, click two seats, **Hold**, **Check out**, **Approve** on LocalPay | The order page goes *confirmed*, with a **Your tickets** button |
| 4 | **Your tickets** | One QR code per seat. What's inside: a signed token (JWS, ES256) naming the ticket, the event and the key. Anyone can verify it with the event's public keys at `GET /events/{eventId}/ticket-keys` (PublicApi.http); only Ticketing can make one (ADR-0051) |
| 5 | Mailpit | The "Your tickets for Demo Concert" email, with the same QR codes as inline images. Sent once, however often the event is delivered (the delivery log, ADR-0052) |
| 6 | Dashboard → **Traces**, search `command StartCheckout` | From the payment to the order confirmed, the tickets issued and the email sent |
| 7 | Ana tries to hold seven more seats | Refused: "You can buy at most 8 tickets for this event, and you already have 2" |
| 8 | `BackOfficeApi.http`: adam adds ben to the demo organizer; ben signs in again | Ben's new token carries the organizer's ID: Organizers keeps Keycloak's groups in step (ADR-0027) |
| 9 | A pull request on GitHub | The checks, among them the Stripe adapter's contract tests against `stripe-mock` (ADR-0050) |

Steps 3, 6 and 7 can also show M1a's expiry and failure scenarios: [the M1a runbook](m1a.md).

## Afterwards

Stop the AppHost (Ctrl+C). Nothing persists: the next run starts with a fresh database, a freshly
imported realm, an empty inbox and new signing keys (ADR-0022).

## If something goes wrong

| Symptom | Fix |
|---|---|
| No email in Mailpit | Tickets come seconds after the payment; the email a moment later. Dashboard → Structured logs, filter `workers` |
| "My tickets" says the tickets are being issued, for long | Workers isn't running or is failing: dashboard → Resources |
| The fan site shows you signed out | It restarted, so its tokens are gone (ADR-0049): sign in again |
| A container doesn't start offline | Its image wasn't pulled: run once online (the day before, step 1) |
| Anything from M1a | [The M1a runbook](m1a.md) |
