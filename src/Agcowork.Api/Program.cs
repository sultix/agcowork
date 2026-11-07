using Agcowork.Application;
using Agcowork.Infrastructure;
using Agcowork.Infrastructure.Persistence;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Adds Npgsql/EF Core tracing and metrics. Health checks are registered by AddInfrastructure.
builder.EnrichNpgsqlDbContext<AgcoworkDbContext>(settings =>
{
    settings.DisableHealthChecks = true;
    // No retrying execution strategy: use cases open transactions directly (see docs/02-ARCHITEKTUR.md §5).
    settings.DisableRetry = true;
});

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference();
}

app.MapDefaultEndpoints();

await app.Services.ApplyMigrationsAsync();

await app.RunAsync();
