using Agcowork.Api.Auth;
using Agcowork.Api.Endpoints;
using Agcowork.Api.Http;
using Agcowork.Api.Mcp;
using Agcowork.Api.Realtime;
using Agcowork.Api.Tenancy;
using Agcowork.Application;
using Agcowork.Infrastructure;
using Agcowork.Infrastructure.Persistence;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Build-time OpenAPI generation (Microsoft.Extensions.ApiDescription.Server) starts the host
// via GetDocument.Insider without a database; skip migrations, bootstrap and the realtime dispatcher there.
var generatingOpenApi = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

builder.AddServiceDefaults();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddCredentialThrottling(builder.Configuration, builder.Environment);

// Adds Npgsql/EF Core tracing and metrics. Health checks are registered by AddInfrastructure.
builder.EnrichNpgsqlDbContext<AgcoworkDbContext>(settings =>
{
    settings.DisableHealthChecks = true;
    // No retrying execution strategy: use cases open transactions directly (see docs/02-ARCHITEKTUR.md §5).
    settings.DisableRetry = true;
});

// Wire JSON (also MCP structured content, see WireJson): snake_case enums; required members always written, also as null;
// optional members (filter nodes, includes such as history) only when set.
builder.Services.ConfigureHttpJsonOptions(options => WireJson.Configure(options.SerializerOptions));

// Every error response is ProblemDetails with a machine-readable "code" (see ProblemDetailsMapping).
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions.TryAdd(
            ProblemDetailsMapping.CodeExtension,
            ProblemDetailsMapping.CodeForStatus(context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode));

        // Binding failures (malformed JSON, wrong content type) say which parameter failed; safe to expose. A JSON value that
        // cannot be read into its field (wrong type, unpaired surrogate) is a field error like those of the use cases.
        if (context.Exception is BadHttpRequestException badRequest)
        {
            if (ProblemDetailsMapping.BindingFieldError(badRequest) is { } fieldError)
            {
                ProblemDetailsMapping.ApplyBindingFieldError(context.ProblemDetails, fieldError);
            }
            else
            {
                context.ProblemDetails.Detail ??= badRequest.Message;
            }
        }
    });
// Binding failures always throw BadHttpRequestException (the default does so only in Development and otherwise writes a bare
// 400): only then the exception handler below can answer them as ProblemDetails with field errors in every environment.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddOpenApi(options => options
    .AddSchemaTransformer<ProblemDetailsSchemaTransformer>()
    .AddOperationTransformer<WorkspaceSlugOpenApiTransformer>()
    .AddOperationTransformer<RetryAfterOpenApiTransformer>());
builder.Services.AddAgcoworkAuthentication(builder.Environment);
builder.Services.AddAgcoworkClientAddress();
builder.Services.AddAgcoworkMcp();
builder.Services.AddAgcoworkRealtime(startDispatcher: !generatingOpenApi);

var app = builder.Build();

// Client address from X-Forwarded-For, only from configured proxies (login throttling, AP-043). First, so everything sees it.
app.UseAgcoworkForwardedHeaders();

app.UseExceptionHandler(new ExceptionHandlerOptions
{
    // Malformed JSON and similar binding failures are client errors, not 500s.
    StatusCodeSelector = exception => exception is BadHttpRequestException badRequest
        ? badRequest.StatusCode
        : StatusCodes.Status500InternalServerError,

    // A client's bad request is no server error: no error log, no exception diagnostics for it.
    SuppressDiagnosticsCallback = context => context.Exception is BadHttpRequestException,
});
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAgcoworkRealtime();

app.MapOpenApi();
if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference();
}

app.MapDefaultEndpoints();
app.MapApiV1();
app.MapAgcoworkMcp();
app.MapAgcoworkRealtime();

if (!generatingOpenApi)
{
    await app.Services.ApplyMigrationsAsync();
    await app.Services.BootstrapIdentityAsync();
}

await app.RunAsync();
