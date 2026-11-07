using System.Text.Json;
using System.Text.Json.Serialization;
using Agcowork.Api.Auth;
using Agcowork.Api.Endpoints;
using Agcowork.Api.Http;
using Agcowork.Api.Tenancy;
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

// Enums travel as snake_case strings ("human", "agent").
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)));

// Every error response is ProblemDetails with a machine-readable "code" (see ProblemDetailsMapping).
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions.TryAdd(
            ProblemDetailsMapping.CodeExtension,
            ProblemDetailsMapping.CodeForStatus(context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode));

        // Binding failures (malformed JSON, wrong content type) say which parameter failed; safe to expose.
        if (context.Exception is BadHttpRequestException badRequest)
        {
            context.ProblemDetails.Detail ??= badRequest.Message;
        }
    });
builder.Services.AddOpenApi(options => options
    .AddSchemaTransformer<ProblemDetailsSchemaTransformer>()
    .AddOperationTransformer<WorkspaceSlugOpenApiTransformer>());
builder.Services.AddAgcoworkAuthentication(builder.Environment);

var app = builder.Build();

app.UseExceptionHandler(new ExceptionHandlerOptions
{
    // Malformed JSON and similar binding failures are client errors, not 500s.
    StatusCodeSelector = exception => exception is BadHttpRequestException badRequest
        ? badRequest.StatusCode
        : StatusCodes.Status500InternalServerError,
});
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantResolutionMiddleware>();

app.MapOpenApi();
if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference();
}

app.MapDefaultEndpoints();
app.MapApiV1();

// Build-time OpenAPI generation (Microsoft.Extensions.ApiDescription.Server) starts the host
// via GetDocument.Insider without a database; skip migrations and bootstrap there.
if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name != "GetDocument.Insider")
{
    await app.Services.ApplyMigrationsAsync();
    await app.Services.BootstrapIdentityAsync();
}

await app.RunAsync();
