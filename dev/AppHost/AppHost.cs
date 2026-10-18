// Runs everything needed for day-to-day development (ADR-0020). Nothing persists between runs:
// no data volumes, a fresh database and a freshly imported Keycloak realm every time (ADR-0022).
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres");
var database = postgres.AddDatabase("eventticketing");

// The development realm: roles, one organizer group, test users ana, ben (fans), olga (organizer),
// adam (admin), passwords equal to the user names. A fixed port keeps token URLs stable in .http files.
var keycloak = builder.AddKeycloak("keycloak", port: 8180)
    .WithRealmImport("./Realms");

// The APIs validate tokens with standard OIDC discovery against the realm (ADR-0027).
var authority = ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/eventticketing");

builder.AddProject<Projects.LocalPay>("localpay")
    .WithHttpHealthCheck("/health");

// Migrations run first; every other host starts only after the Migrator has exited successfully.
var migrator = builder.AddProject<Projects.Migrator>("migrator")
    .WithReference(database)
    .WaitFor(database);

builder.AddProject<Projects.PublicApi>("publicapi")
    .WithEnvironment("Authentication__Authority", authority)
    .WithEnvironment("Authentication__RequireHttpsMetadata", "false")
    .WithHttpHealthCheck("/health")
    .WaitFor(keycloak)
    .WaitForCompletion(migrator);

builder.AddProject<Projects.BackOfficeApi>("backofficeapi")
    .WithEnvironment("Authentication__Authority", authority)
    .WithEnvironment("Authentication__RequireHttpsMetadata", "false")
    .WithHttpHealthCheck("/health")
    .WaitFor(keycloak)
    .WaitForCompletion(migrator);

builder.AddProject<Projects.WebhookIngress>("webhookingress")
    .WithHttpHealthCheck("/health")
    .WaitForCompletion(migrator);

builder.AddProject<Projects.Workers>("workers")
    .WithHttpHealthCheck("/health")
    .WaitForCompletion(migrator);

builder.Build().Run();
