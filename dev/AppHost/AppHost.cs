// Runs everything needed for day-to-day development (ADR-0020). Nothing persists between runs:
// no data volumes, a fresh database and a freshly imported Keycloak realm every time (ADR-0022).
var builder = DistributedApplication.CreateBuilder(args);

// Demo mode (dotnet run --project dev/AppHost -- --Demo=true): the demo script's short timings, so
// expiry can be shown live. Holds last 1 minute and checkouts 2 (docs/demos/m1a.md).
var demo = string.Equals(builder.Configuration["Demo"], "true", StringComparison.OrdinalIgnoreCase);
IResourceBuilder<ProjectResource> WithTimings(IResourceBuilder<ProjectResource> host) => demo
    ? host.WithEnvironment("Inventory__HoldLifetime", "00:01:00").WithEnvironment("Checkout__PaymentDeadline", "00:02:00")
    : host;

var postgres = builder.AddPostgres("postgres");
var database = postgres.AddDatabase("eventticketing");

// The fan site's client secret at Keycloak (a confidential client, ADR-0049). Generated fresh on every
// run like every other secret in the disposable lab (ADR-0025); the realm import reads it from the
// environment (${FANSITE_CLIENT_SECRET} in the realm file).
var fanSiteSecret = builder.AddParameter(
    "fansite-client-secret",
    new GenerateParameterDefault { MinLength = 32, Special = false },
    secret: true);

// The development realm: roles, one organizer group, test users ana, ben, cid, dan, eva, fil (fans), olga (organizer),
// adam (admin), passwords equal to the user names, and the fan site's client. A fixed port keeps token URLs stable in
// .http files.
// Plain HTTP even when the developer certificate is trusted: otherwise Aspire serves Keycloak over
// HTTPS on `dotnet run` (but not in tests), and http://localhost:8180 in .http files would break.
// The API is marked experimental (ASPIRECERTIFICATES001); revisit when Aspire settles it.
#pragma warning disable ASPIRECERTIFICATES001
var keycloak = builder.AddKeycloak("keycloak", port: 8180)
    .WithRealmImport("./Realms")
    .WithEnvironment("FANSITE_CLIENT_SECRET", fanSiteSecret)
    .WithoutHttpsCertificate();
#pragma warning restore ASPIRECERTIFICATES001

// The APIs validate tokens with standard OIDC discovery against the realm (ADR-0027).
var authority = ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/eventticketing");

// LocalPay signs its webhooks with this secret; the hosts that verify them get the same value.
// Generated fresh on every run, like every other secret in the disposable lab (ADR-0025).
var webhookSecret = builder.AddParameter(
    "localpay-webhook-secret",
    new GenerateParameterDefault { MinLength = 32, Special = false },
    secret: true);

// Migrations run first; every other host starts only after the Migrator has exited successfully.
var migrator = builder.AddProject<Projects.Migrator>("migrator")
    .WithReference(database)
    .WaitFor(database);

var webhookIngress = WithTimings(builder.AddProject<Projects.WebhookIngress>("webhookingress"))
    .WithReference(database)
    .WithEnvironment("Payments__LocalPay__WebhookSecret", webhookSecret)
    .WithHttpHealthCheck("/health")
    .WaitForCompletion(migrator);

var localPay = builder.AddProject<Projects.LocalPay>("localpay")
    .WithEnvironment("Webhooks__Url", ReferenceExpression.Create($"{webhookIngress.GetEndpoint("http")}/webhooks/localpay"))
    .WithEnvironment("Webhooks__Secret", webhookSecret)
    .WithHttpHealthCheck("/health");

var publicApi = WithTimings(builder.AddProject<Projects.PublicApi>("publicapi"))
    .WithReference(database)
    .WithReference(localPay)
    .WithEnvironment("Authentication__Authority", authority)
    .WithEnvironment("Authentication__RequireHttpsMetadata", "false")
    .WithEnvironment("Payments__LocalPay__BaseUrl", "http://localpay")
    .WithHttpHealthCheck("/health")
    .WaitFor(keycloak)
    .WaitForCompletion(migrator);

// The fan site (ADR-0049): uses the product only through PublicApi, signs fans in at Keycloak.
// Port 5100 from its launch profile; system tests run it on a random port.
var fanSite = builder.AddProject<Projects.FanSite>("fansite")
    .WithReference(publicApi)
    .WithEnvironment("Authentication__Authority", authority)
    .WithEnvironment("Authentication__RequireHttpsMetadata", "false")
    .WithEnvironment("Authentication__ClientSecret", fanSiteSecret)
    .WithHttpHealthCheck("/health")
    .WaitFor(keycloak)
    .WaitFor(publicApi);

// Keycloak accepts sign-in redirects only to the fan site's address, whatever port it got
// (${FANSITE_URL} in the realm file). Built from the port: an endpoint passed to a container
// resolves to the Docker host's name, while the browser calls localhost.
keycloak.WithEnvironment("FANSITE_URL", ReferenceExpression.Create($"http://localhost:{fanSite.GetEndpoint("http").Property(EndpointProperty.Port)}"));

// After paying, LocalPay sends the fan back to the fan site's order page, which follows the order.
publicApi.WithEnvironment("Checkout__ReturnUrlTemplate", ReferenceExpression.Create($"{fanSite.GetEndpoint("http")}/orders/{{orderId}}"));

var backOfficeApi = WithTimings(builder.AddProject<Projects.BackOfficeApi>("backofficeapi"))
    .WithReference(database)
    .WithEnvironment("Authentication__Authority", authority)
    .WithEnvironment("Authentication__RequireHttpsMetadata", "false")
    .WithHttpHealthCheck("/health")
    .WaitFor(keycloak)
    .WaitForCompletion(migrator);

// Workers talks to LocalPay too: cancelling payments at the deadline, status checks, refunds.
var workers = WithTimings(builder.AddProject<Projects.Workers>("workers"))
    .WithReference(database)
    .WithReference(localPay)
    .WithEnvironment("Payments__LocalPay__BaseUrl", "http://localpay")
    .WithEnvironment("Payments__LocalPay__WebhookSecret", webhookSecret)
    .WithHttpHealthCheck("/health")
    .WaitForCompletion(migrator);

// The demo seeder (T1): press Start in the dashboard when the demo begins. It prepares a venue and
// an event on sale through the APIs, then exits. `dotnet run --project tooling/seed` does the same.
builder.AddProject<Projects.Seed>("seed")
    .WithEnvironment("BACKOFFICEAPI_URL", backOfficeApi.GetEndpoint("http"))
    .WithEnvironment("PUBLICAPI_URL", publicApi.GetEndpoint("http"))
    .WithEnvironment("KEYCLOAK_URL", keycloak.GetEndpoint("http"))
    .WaitFor(backOfficeApi)
    .WaitFor(publicApi)
    .WaitFor(workers)
    .WithExplicitStart();

builder.Build().Run();
