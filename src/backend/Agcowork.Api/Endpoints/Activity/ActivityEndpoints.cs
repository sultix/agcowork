using System.ComponentModel;
using Agcowork.Api.Http;
using Agcowork.Application.Activity;
using Agcowork.Domain.Activity;
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
                "Activity events: who changed which record, space, collection or member, with a diff ('old'/'new' per changed "
                + "field; for record bodies 'bodyChanged' and 'bodyDelta' {added, removed} characters). 'targetTitle' is the current "
                + "title of the record, or name of the space or collection, null if the target no longer exists (and for members and "
                + "views). Filters: spaceId, collectionId, actorId, targetId (the events of one record, space or collection), recordId "
                + "(the whole history of one record: its own events and those of its comments), since "
                + "(ISO 8601, e.g. 2026-11-06T00:00:00Z), types (repeatable: " + string.Join(", ", ActivityTypes.All) + "). "
                + "Two modes. Without afterSeq: newest first, paginated with 'limit' (1..200) and 'cursor' (nextCursor of the previous "
                + "page); for a look back ('what happened since yesterday?'). With afterSeq: forward, in commit-safe order (transaction "
                + "order), only events that are final; for keeping up with changes. Start with afterSeq=0 (whole log) or with 'lastSeq' "
                + "of a newest-first response (from now on), then call again with the 'lastSeq' of each response; with a full page call "
                + "again at once, otherwise poll. Only lastSeq is a safe cursor: never pass the seq of an item, it can skip events. "
                + "This never skips an event, even when transactions commit in another order than their seq: a lower seq can follow a "
                + "higher one, and lastSeq is the seq of the last event, not always the highest. Do not poll with 'since': 'at' is taken "
                + "before the commit, so an event can appear later with an 'at' before your last poll. afterSeq and cursor do not "
                + "combine. Requires read.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return workspace;
    }

    private static async Task<Results<Ok<ActivityPage>, ProblemHttpResult>> ListAsync(
        ListActivity useCase,
        [FromQuery] Guid? spaceId,
        [FromQuery] Guid? collectionId,
        [FromQuery] Guid? actorId,
        [FromQuery] DateTimeOffset? since,
        [FromQuery] string[]? types,
        [FromQuery] int? limit,
        [FromQuery] string? cursor,
        [FromQuery, Description("Read forward after this position: 0 for the whole log, otherwise 'lastSeq' of the previous response.")] long? afterSeq,
        [FromQuery, Description("Only events of this target: record, space, collection or view id; for members the actor id.")] Guid? targetId,
        [FromQuery, Description("The history of one record: its own events and the events of its comments (comment.*). Combines with the other filters.")] Guid? recordId,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(
            new ListActivityRequest(spaceId, collectionId, actorId, since, types, limit, cursor, afterSeq, targetId, recordId),
            cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}
