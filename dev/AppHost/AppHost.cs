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

// The development realm: roles, one organizer group, test users ana, ben, cid, dan, eva, fil (fans), olga (organizer),
// adam (admin), passwords equal to the user names. A fixed port keeps token URLs stable in .http files.
// Plain HTTP even when the developer certificate is trusted: otherwise Aspire serves Keycloak over
// HTTPS on `dotnet run` (but not in tests), and http://localhost:8180 in .http files would break.
// The API is marked experimental (ASPIRECERTIFICATES001); revisit when Aspire settles it.
#pragma warning disable ASPIRECERTIFICATES001
var keycloak = builder.AddKeycloak("keycloak", port: 8180)
    .WithRealmImport("./Realms")
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

var publicApi = WithTimings(builder.AddProject<Projects.PublicApi>("publicapi"));
publicApi
    .WithReference(database)
    .WithReference(localPay)
    .WithEnvironment("Authentication__Authority", authority)
    .WithEnvironment("Authentication__RequireHttpsMetadata", "false")
    .WithEnvironment("Payments__LocalPay__BaseUrl", "http://localpay")
    .WithEnvironment("Checkout__ReturnUrlTemplate", ReferenceExpression.Create($"{publicApi.GetEndpoint("http")}/orders/{{orderId}}"))
    .WithHttpHealthCheck("/health")
    .WaitFor(keycloak)
    .WaitForCompletion(migrator);

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
