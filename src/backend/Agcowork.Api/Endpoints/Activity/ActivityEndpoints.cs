using Agcowork.Api.Http;
using Agcowork.Application.Activity;
using Agcowork.Application.Common.Paging;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Agcowork.Api.Endpoints.Activity;

/// <summary>The activity feed of the workspace (<c>activity_recent</c>).</summary>
internal static class ActivityEndpoints
{
    public static RouteGroupBuilder MapActivityEndpoints(this RouteGroupBuilder workspace)
    {
        workspace.MapGet("/activity", ListAsync)
            .WithTags("Activity")
            .WithName("ListActivity")
            .WithSummary("What happened in the workspace")
            .WithDescription(
                "Activity events, newest first: who changed which record, space, collection or member, with a diff ('old'/'new' per "
                + "changed field; for record bodies 'bodyChanged' and 'bodyDelta' {added, removed} characters). Filters: spaceId, "
                + "collectionId, actorId, since (ISO 8601, e.g. 2026-11-06T00:00:00Z), types (repeatable: record.created, "
                + "record.updated, record.appended, record.archived, record.restored, space.created, space.updated, space.archived, "
                + "collection.created, collection.updated, collection.schema_changed, collection.archived, member.added, "
                + "member.updated, member.removed). Paginated with 'limit' (1..200) and 'cursor'. Requires read.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return workspace;
    }

    private static async Task<Results<Ok<Page<ActivityEventDto>>, ProblemHttpResult>> ListAsync(
        ListActivity useCase,
        [FromQuery] Guid? spaceId,
        [FromQuery] Guid? collectionId,
        [FromQuery] Guid? actorId,
        [FromQuery] DateTimeOffset? since,
        [FromQuery] string[]? types,
        [FromQuery] int? limit,
        [FromQuery] string? cursor,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new ListActivityRequest(spaceId, collectionId, actorId, since, types, limit, cursor), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}
