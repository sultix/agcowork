using Agcowork.Api.Http;
using Agcowork.Application.Spaces;
using Agcowork.Application.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Agcowork.Api.Endpoints.Spaces;

/// <summary>Spaces of the workspace. Thin: bind, call the use case, map the result.</summary>
internal static class SpaceEndpoints
{
    public static RouteGroupBuilder MapSpaceEndpoints(this RouteGroupBuilder workspace)
    {
        var spaces = workspace.MapGroup("/spaces").WithTags("Spaces");

        spaces.MapGet("/", ListAsync)
            .WithName("ListSpaces")
            .WithSummary("List spaces")
            .WithDescription(
                "Spaces (areas such as projects or teams) of the workspace, ordered by 'sortOrder', then name. Each space groups "
                + "collections; read 'description' to decide where things belong. Archived spaces only with includeArchived=true. Requires read.");

        spaces.MapPost("/", CreateAsync)
            .WithName("CreateSpace")
            .WithSummary("Create a space")
            .WithDescription(
                "Creates a space. 'name' is required (max. 200); 'description' (max. 2000) should say what the space is for, agents "
                + "read it; 'icon' is an emoji or icon name; 'sortOrder' defaults to after the last space. Requires write.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        spaces.MapGet("/{spaceId:guid}", GetAsync)
            .WithName("GetSpace")
            .WithSummary("Get a space")
            .WithDescription("One space, archived ones included. Requires read. Unknown id: 404.");

        spaces.MapPatch("/{spaceId:guid}", UpdateAsync)
            .WithName("UpdateSpace")
            .WithSummary("Update a space")
            .WithDescription(
                "Partial update of 'name', 'description', 'icon' (\"\" clears description and icon) and 'sortOrder'; omitted fields stay "
                + "unchanged. Requires write. Archived space: 409 'space_archived'.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        spaces.MapPost("/{spaceId:guid}/archive", ArchiveAsync)
            .WithName("ArchiveSpace")
            .WithSummary("Archive a space")
            .WithDescription(
                "Makes the space read-only and hides it from the list; it gets no new collections, its collections stay as they are. "
                + "Requires admin. Already archived: 409 'space_archived'.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return workspace;
    }

    private static async Task<Results<Ok<IReadOnlyList<SpaceDto>>, ProblemHttpResult>> ListAsync(
        ListSpaces useCase,
        [FromQuery] bool? includeArchived,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new ListSpacesRequest(includeArchived ?? false), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Created<SpaceDto>, ProblemHttpResult>> CreateAsync(
        CreateSpaceRequest request,
        CreateSpace useCase,
        ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(request, cancellationToken);
        return result.IsSuccess
            ? TypedResults.Created($"{ApiV1.Prefix}/w/{tenant.Slug}/spaces/{result.Value.Id}", result.Value)
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<SpaceDto>, ProblemHttpResult>> GetAsync(Guid spaceId, GetSpace useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new GetSpaceRequest(spaceId), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<SpaceDto>, ProblemHttpResult>> UpdateAsync(
        Guid spaceId,
        UpdateSpaceBody body,
        UpdateSpace useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new UpdateSpaceRequest(spaceId, body.Name, body.Description, body.Icon, body.SortOrder), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<SpaceDto>, ProblemHttpResult>> ArchiveAsync(Guid spaceId, ArchiveSpace useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new ArchiveSpaceRequest(spaceId), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}

/// <summary>Body of <c>PATCH /spaces/{spaceId}</c>; null leaves a field unchanged.</summary>
/// <param name="Name">New display name.</param>
/// <param name="Description">New description; "" clears it.</param>
/// <param name="Icon">New icon; "" clears it.</param>
/// <param name="SortOrder">New position in the navigation.</param>
internal sealed record UpdateSpaceBody(string? Name = null, string? Description = null, string? Icon = null, int? SortOrder = null);
