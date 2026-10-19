# 0049. The fan site: Blazor Interactive Server, with the fans' tokens on the server

- **Status:** Agreed
- **Date:** 2026-10-19
- **Related:** [0008](0008-hosts.md) · [0021](0021-product-tooling-platform-separation.md) · [0024](0024-domain-scope-m1-m6.md) · [0025](0025-object-storage-and-secrets.md) · [0027](0027-identity-provider-keycloak.md) · [0035](0035-blazor-for-all-uis.md) · [0037](0037-kiota-api-clients.md) · [0043](0043-how-a-checkout-ends.md) · [M1a plan](../milestones/m1a.md) (U1)

## Context

M1a showed its flows through `.http` files ([0035](0035-blazor-for-all-uis.md)): the demo meant
copying IDs between requests and signing in by hand. Every API flow a first fan UI needs
exists, so the owner brought the fan site forward from M1b on 2026-10-19 (decisions D1–D5).

Two things shape it:

- Fans sign in with standard OpenID Connect ([0027](0027-identity-provider-keycloak.md)), and
  access tokens last 5 minutes.
- A Blazor Server circuit is a long-lived connection. It has no HttpContext, so it can't read or
  renew cookies, and a token captured when the circuit started is stale 5 minutes later.

## Decision

**What it is**

- A **Blazor Web App** in `product/src/Frontends/FanSite`, **Interactive Server** on every page
  (prerendered, then interactive). Under Aspire it is the `fansite` resource at
  http://localhost:5100 (a random port in system tests).
- It uses the product **only through PublicApi's HTTP API**, with a Kiota client generated into
  the site from the committed OpenAPI document (an update to [0037](0037-kiota-api-clients.md)).
  It references ServiceDefaults and no module or Shared project; an architecture test checks it.
- First slice: event list and search; event page with the seat map (click seats, or "best
  available" in a section), the fan's hold with a countdown, release and checkout; the return
  page, which follows the order until it settles ([0043](0043-how-a-checkout-ends.md)).
  Not yet: my tickets (Ticketing), my orders (needs an endpoint), the queue page (WaitingRoom).
- The checkout's return URL is the fan site's order page.

**Signing in**

- Authorization code flow with PKCE, as a **confidential client** (`eventticketing-fansite`).
  Its secret is generated on every run and handed to both Keycloak and the site
  ([0025](0025-object-storage-and-secrets.md)). The realm file reads the secret, and the site's
  address for the redirect URIs, from the environment, so sign-in works on any port.
- ASP.NET Core uses pushed authorization requests (PAR) when Keycloak offers them, so Keycloak
  checks the redirect URI before the fan sees a login page.
- **Tokens never reach the browser.** The cookie holds a random session key. The tokens live on
  the server in `FanTokenStore`: encrypted with Data Protection, in `IDistributedCache`.
- A scoped `FanAccessToken` finds the fan through `AuthenticationStateProvider`, which works in
  a circuit and while prerendering. It refreshes the access token at the token endpoint when
  less than 30 seconds are left, one refresh per session at a time.
- On every request, a cookie whose tokens are gone (signed out, refresh refused, site restarted)
  signs the fan out, so nobody looks signed in without access.
- Sign-in and sign-out are plain endpoints, because a circuit can't set cookies. Sign-out is a
  form POST with an antiforgery token, and it ends the Keycloak session too.
- The sign-in callback uses `response_mode=query`, so lax cookies work over plain HTTP in development.

**Smaller rules**

- The site retries only safe HTTP methods; a hold must not be placed twice.
- The fan's current hold per event is kept in the browser (`ProtectedLocalStorage`): PublicApi has
  no "my current hold" endpoint yet.
- The map shows availability that is at most 5 seconds old; PublicApi decides who gets a seat.

## Alternatives considered

| Alternative | Why not |
|---|---|
| A throwaway demo tool in `tooling/` that signs in with the password grant | Faster, but thrown away later, and it teaches a sign-in flow real browser apps must not use |
| Blazor WebAssembly with a backend-for-frontend holding the tokens | More moving parts (a proxy, antiforgery on every call); no gain for this UI |
| Static server rendering with plain forms only | Simplest sign-in, and no sticky sessions; but every seat click would be a page load |
| Tokens in the authentication cookie (`SaveTokens`) | A circuit can't rewrite the cookie, so refreshed tokens would be lost; and a bigger cookie |
| `Duende.AccessTokenManagement` (Apache 2.0) for the token store and refresh | A good library, but the store and refresh are about 150 lines here and teach how OIDC sessions work; worth revisiting if the site grows |
| A public client with PKCE only | The site is a server, which can keep a secret; a confidential client is the stronger setup |
| Hand-written HTTP calls to PublicApi | Drift from the contract unnoticed, which is why [0037](0037-kiota-api-clients.md) exists |

## Consequences

- One stack for the UI, in C#, and the API's contract compiles into it.
- **Scaling the fan site** (M2+) needs three things, all configuration: the token store in Valkey,
  a shared Data Protection key ring, and sticky sessions for circuits. Until then it runs as one
  instance, and a restart signs fans out. If Keycloak's session is still alive, signing in
  again needs no password.
- The demo runs in the browser; the `.http` files remain for the API itself.
- A hold made in another browser is unknown to this one until it expires (it still blocks a new
  hold, with PublicApi's message). A "my current hold" endpoint would fix it.
- No browser test yet: system tests check the site over HTTP (pages, sign-in). A Playwright
  test is a separate decision.
