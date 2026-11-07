using Agcowork.Api.Auth;
using Agcowork.Api.Endpoints.Activity;
using Agcowork.Api.Endpoints.Collections;
using Agcowork.Api.Endpoints.Identity;
using Agcowork.Api.Endpoints.Records;
using Agcowork.Api.Endpoints.Spaces;
using Agcowork.Api.Endpoints.Views;
using Agcowork.Api.Endpoints.Workspaces;
using Agcowork.Api.Tenancy;

namespace Agcowork.Api.Endpoints;

/// <summary>
/// Root of the REST API. Everything under <c>/api/v1</c> requires an authenticated actor unless an
/// endpoint opts out explicitly with <c>AllowAnonymous()</c>.
/// </summary>
internal static class ApiV1
{
    public const string Prefix = "/api/v1";

    /// <summary>Route of the workspace group; everything workspace-bound goes below it.</summary>
    public const string WorkspacePrefix = "/w/{" + TenantEndpointMetadata.SlugRouteValue + "}";

    public static IEndpointRouteBuilder MapApiV1(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(Prefix)
            .RequireAuthorization(AuthPolicies.Authenticated)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        api.MapAuthEndpoints();
        api.MapMeEndpoints();
        api.MapActorEndpoints();
        api.MapApiKeyEndpoints();
        api.MapWorkspaceInstanceEndpoints();

        // Tenant scope (architecture §2a): TenantResolutionMiddleware resolves {workspaceSlug} and the membership
        // before any endpoint here runs. Unknown workspace, no membership or a key for another workspace: 404.
        var workspace = api.MapGroup(WorkspacePrefix)
            .WithMetadata(TenantEndpointMetadata.Instance)
            .ProducesProblem(StatusCodes.Status404NotFound);

        workspace.MapWorkspaceEndpoints();
        workspace.MapMemberEndpoints();
        workspace.MapSpaceEndpoints();
        workspace.MapCollectionEndpoints();
        workspace.MapViewEndpoints();
        workspace.MapRecordEndpoints();
        workspace.MapActivityEndpoints();

        return app;
    }
}
