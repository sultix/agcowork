using System.Text.Json.Nodes;
using Agcowork.Domain.Activity;

namespace Agcowork.Application.Activity;

/// <summary>One entry of the activity feed.</summary>
/// <param name="Seq">Position in the workspace's log (monotonic); the feed is ordered by it, newest first.</param>
/// <param name="Id">Event id.</param>
/// <param name="Type">
/// What happened: record.created, record.updated, record.appended, record.archived, record.restored, space.created,
/// space.updated, space.archived, collection.created, collection.updated, collection.schema_changed, collection.archived,
/// member.added, member.updated, member.removed.
/// </param>
/// <param name="ActorId">Who did it.</param>
/// <param name="TargetType">record, space, collection or member.</param>
/// <param name="TargetId">Id of the target; for members the actor id.</param>
/// <param name="TargetTitle">
/// The current title of a record, or name of a space or collection, read when the feed is listed (so a renamed
/// target shows its new name). Null when the target no longer exists, and for members and views.
/// </param>
/// <param name="SpaceId">Space of the target, if any.</param>
/// <param name="CollectionId">Collection of the target, if any.</param>
/// <param name="Diff">
/// What changed. Changed fields as <c>{ "old": …, "new": … }</c>; records: <c>title</c>, <c>properties.&lt;key&gt;</c>, and
/// <c>bodyChanged</c>/<c>bodyDelta</c> (<c>{ added, removed }</c> characters) instead of the body.
/// </param>
/// <param name="At">When it happened (UTC).</param>
public sealed record ActivityEventDto(
    long Seq,
    Guid Id,
    string Type,
    Guid ActorId,
    string TargetType,
    Guid TargetId,
    string? TargetTitle,
    Guid? SpaceId,
    Guid? CollectionId,
    JsonObject Diff,
    DateTimeOffset At)
{
    /// <param name="activity">The stored event.</param>
    /// <param name="targetTitle">Current title of the target, if resolved.</param>
    public static ActivityEventDto From(ActivityEvent activity, string? targetTitle = null)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return new ActivityEventDto(
            activity.Seq,
            activity.Id,
            activity.Type,
            activity.ActorId,
            activity.TargetType,
            activity.TargetId,
            targetTitle,
            activity.SpaceId,
            activity.CollectionId,
            (JsonObject)activity.Diff.DeepClone(),
            activity.At);
    }
}
