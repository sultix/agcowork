var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL 17 per E8. Persistent container plus named volume keeps data across AppHost restarts.
var postgres = builder.AddPostgres("postgres")
    .WithImageTag("17")
    .WithDataVolume("agcowork-postgres-data")
    .WithLifetime(ContainerLifetime.Persistent);

var database = postgres.AddDatabase("agcowork");

var api = builder.AddProject<Projects.Agcowork_Api>("api")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/health/ready");

// Angular dev server. WithReference(api) injects the dynamic API endpoint as services__api__http__0,
// which proxy.conf.js reads. Aspire assigns the port; it is forwarded to `ng serve` via --port.
builder.AddJavaScriptApp("web", "../../frontend", "start")
    .WithNpm(install: true)
    .WithReference(api)
    .WaitFor(api)
    .WithHttpEndpoint(env: "PORT")
    .WithArgs(ctx =>
    {
        // npm forwards everything after "--" to `ng serve`.
        var endpoint = ((IResourceWithEndpoints)ctx.Resource).GetEndpoint("http");
        ctx.Args.Add("--");
        ctx.Args.Add("--port");
        ctx.Args.Add(endpoint.Property(EndpointProperty.TargetPort));
    })
    .WithExternalHttpEndpoints();

await builder.Build().RunAsync();
