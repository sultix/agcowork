using Agcowork.Api.Http;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;

namespace Agcowork.Api.Tenancy;

/// <summary>
/// Marks endpoints that run inside a workspace (<c>/api/v1/w/{workspaceSlug}/…</c>): only these get a tenant.
/// Every other workspace-bound entry point must carry it too, in particular the MCP endpoint
/// (<c>/w/{workspaceSlug}/mcp</c>, outside <c>/api/v1</c>, AP-011), and its route must name the slug
/// <see cref="SlugRouteValue"/>; otherwise its use cases run without a tenant and see no data.
/// </summary>
internal sealed class TenantEndpointMetadata
{
    public const string SlugRouteValue = "workspaceSlug";

    public static TenantEndpointMetadata Instance { get; } = new();
}

/// <summary>
/// Tenancy step of the request pipeline (architecture §2a, step 3). For endpoints with
/// <see cref="TenantEndpointMetadata"/>: resolve slug and membership via <see cref="ResolveTenant"/>,
/// answer 404 when the workspace is unknown, the caller is no member or the key is restricted to
/// another workspace, and otherwise set the tenant for the rest of the request. Archived workspaces
/// are not decided here (no guessing by HTTP verb): <see cref="ITenantContext.IsArchived"/> is passed on and
/// every writing use case answers 409 <c>workspace_archived</c> through <c>RequireWritable</c>.
/// </summary>
/// <remarks>
/// A middleware rather than an endpoint filter: minimal APIs bind handler services (the use cases,
/// which inject <see cref="ITenantContext"/>) before endpoint filters run, so the tenant has to
/// exist before the endpoint is invoked. Runs after authentication and authorization, so anonymous
/// callers get 401 before anything about workspaces is looked up.
/// </remarks>
internal sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        ResolveTenant resolveTenant,
        ITenantContextAccessor tenantAccessor,
        IProblemDetailsService problemDetails)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<TenantEndpointMetadata>() is null)
        {
            await next(context);
            return;
        }

        var slug = context.Request.RouteValues[TenantEndpointMetadata.SlugRouteValue] as string ?? string.Empty;
        var result = await resolveTenant.HandleAsync(new ResolveTenantRequest(slug), context.RequestAborted);
        if (result.IsFailure)
        {
            await WriteProblemAsync(context, problemDetails, result.Error);
            return;
        }

        tenantAccessor.SetTenant(result.Value);
        await next(context);
    }

    private static async Task WriteProblemAsync(HttpContext context, IProblemDetailsService problemDetails, Error error)
    {
        var problem = ProblemDetailsMapping.ToProblemDetails(error);
        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        await problemDetails.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
    }
}
