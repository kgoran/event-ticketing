// Runs everything needed for day-to-day development (ADR-0020). Nothing persists between runs:
// no data volumes, a fresh database and a freshly imported Keycloak realm every time (ADR-0022).
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres");
var database = postgres.AddDatabase("eventticketing");

// The realm (clients, roles, test users) is filled in by backlog item K1; the hosts reference
// Keycloak and LocalPay once they use them (K1, P1).
builder.AddKeycloak("keycloak")
    .WithRealmImport("./Realms");

builder.AddProject<Projects.LocalPay>("localpay")
    .WithHttpHealthCheck("/health");

// Migrations run first; every other host starts only after the Migrator has exited successfully.
var migrator = builder.AddProject<Projects.Migrator>("migrator")
    .WithReference(database)
    .WaitFor(database);

builder.AddProject<Projects.PublicApi>("publicapi")
    .WithHttpHealthCheck("/health")
    .WaitForCompletion(migrator);

builder.AddProject<Projects.BackOfficeApi>("backofficeapi")
    .WithHttpHealthCheck("/health")
    .WaitForCompletion(migrator);

builder.AddProject<Projects.WebhookIngress>("webhookingress")
    .WithHttpHealthCheck("/health")
    .WaitForCompletion(migrator);

builder.AddProject<Projects.Workers>("workers")
    .WithHttpHealthCheck("/health")
    .WaitForCompletion(migrator);

builder.Build().Run();
