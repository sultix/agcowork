using Agcowork.Api.Auth;
using Agcowork.Api.Http;
using Agcowork.Application.Workspaces;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Agcowork.Api.Endpoints.Workspaces;

/// <summary>
/// Workspaces on the instance level (list own, create) and inside the workspace route group
/// (get, update, archive). Thin: bind, call the use case, map the result.
/// </summary>
internal static class WorkspaceEndpoints
{
    public static RouteGroupBuilder MapWorkspaceInstanceEndpoints(this RouteGroupBuilder api)
    {
        var workspaces = api.MapGroup("/workspaces").WithTags("Workspaces");

        workspaces.MapGet("/", ListAsync)
            .WithName("ListMyWorkspaces")
            .WithSummary("List my workspaces")
            .WithDescription(
                "Workspaces the caller is a member of, ordered by name, each with the caller's 'role' (owner, admin, editor, viewer) "
                + "and effective 'scopes' (read, write, schema, admin; narrowed by the API key, if it is restricted). Archived workspaces "
                + "are included with 'archivedAt'. A key restricted to one workspace sees only that one. Use 'slug' in /api/v1/w/{slug}/….");

        workspaces.MapPost("/", CreateAsync)
            .WithName("CreateWorkspace")
            .WithSummary("Create a workspace")
            .WithDescription(
                "Creates a workspace; the caller becomes its owner. 'slug' (unique, immutable; lowercase letters, digits, single hyphens, "
                + "max. 63) becomes the URL segment /api/v1/w/{slug}. 'name' is required, 'description' optional. 'templateId' accepts "
                + "only 'empty' for now (default). Requires workspaces:create (instance:admin implies it). A taken slug yields 409 'slug_taken'.")
            .RequireAuthorization(AuthPolicies.WorkspacesCreate)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    public static RouteGroupBuilder MapWorkspaceEndpoints(this RouteGroupBuilder workspace)
    {
        var group = workspace.MapGroup(string.Empty).WithTags("Workspaces");

        group.MapGet("/", GetAsync)
            .WithName("GetWorkspace")
            .WithSummary("Get the workspace")
            .WithDescription("The workspace with the caller's role and effective scopes. Requires read. Unknown slug or no membership: 404.");

        group.MapPatch("/", UpdateAsync)
            .WithName("UpdateWorkspace")
            .WithSummary("Update the workspace")
            .WithDescription(
                "Partial update of 'name' and 'description' (\"\" clears the description); omitted fields stay unchanged. The slug cannot "
                + "change. Requires admin (roles admin, owner). Archived workspace: 409 'workspace_archived'.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/archive", ArchiveAsync)
            .WithName("ArchiveWorkspace")
            .WithSummary("Archive the workspace")
            .WithDescription(
                "Makes the workspace read-only: reads keep working, every write answers 409 'workspace_archived'. Owner only. "
                + "Cannot be undone through the API yet.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return workspace;
    }

    private static async Task<Results<Ok<IReadOnlyList<WorkspaceDto>>, ProblemHttpResult>> ListAsync(ListMyWorkspaces useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new ListMyWorkspacesRequest(), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Created<WorkspaceDto>, ProblemHttpResult>> CreateAsync(
        CreateWorkspaceRequest request,
        CreateWorkspace useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(request, cancellationToken);
        return result.IsSuccess
            ? TypedResults.Created($"{ApiV1.Prefix}/w/{result.Value.Slug}", result.Value)
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<WorkspaceDto>, ProblemHttpResult>> GetAsync(GetWorkspace useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new GetWorkspaceRequest(), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<WorkspaceDto>, ProblemHttpResult>> UpdateAsync(
        UpdateWorkspaceRequest request,
        UpdateWorkspace useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(request, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<WorkspaceDto>, ProblemHttpResult>> ArchiveAsync(ArchiveWorkspace useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new ArchiveWorkspaceRequest(), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}
