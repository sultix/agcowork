using System.Text.Json.Nodes;
using Agcowork.Domain.Common;

namespace Agcowork.Domain.Activity;

/// <summary>
/// One entry of the append-only activity log of a workspace: who did what to which target, with a diff.
/// Ordered by <see cref="Seq"/> (assigned by the database, monotonic), never by the id (architecture §5).
/// </summary>
public sealed class ActivityEvent : ITenantEntity
{
    public const int TypeMaxLength = 64;
    public const int TargetTypeMaxLength = 32;

    private ActivityEvent(
        Guid id,
        Guid workspaceId,
        string type,
        Guid actorId,
        string targetType,
        Guid targetId,
        Guid? spaceId,
        Guid? collectionId,
        JsonObject diff,
        DateTimeOffset at)
    {
        Id = id;
        WorkspaceId = workspaceId;
        Type = type;
        ActorId = actorId;
        TargetType = targetType;
        TargetId = targetId;
        SpaceId = spaceId;
        CollectionId = collectionId;
        Diff = diff;
        At = at;
    }

    // For EF Core materialization.
    private ActivityEvent()
    {
        Type = string.Empty;
        TargetType = string.Empty;
        Diff = [];
    }

    public Guid Id { get; private set; }

    public Guid WorkspaceId { get; private set; }

    /// <summary>Position in the workspace's log, assigned on insert (identity column). Use it for order and cursors.</summary>
    public long Seq { get; private set; }

    /// <summary>One of <see cref="ActivityTypes"/>, e.g. <c>record.updated</c>.</summary>
    public string Type { get; private set; }

    public Guid ActorId { get; private set; }

    /// <summary>One of <see cref="ActivityTargets"/>, e.g. <c>record</c>.</summary>
    public string TargetType { get; private set; }

    public Guid TargetId { get; private set; }

    public Guid? SpaceId { get; private set; }

    public Guid? CollectionId { get; private set; }

    /// <summary>What changed; format per type (see <c>Records.RecordDiff</c> for records). Empty object when nothing to show.</summary>
    public JsonObject Diff { get; private set; }

    public DateTimeOffset At { get; private set; }

    public static ActivityEvent Create(
        Guid workspaceId,
        string type,
        Guid actorId,
        string targetType,
        Guid targetId,
        Guid? spaceId,
        Guid? collectionId,
        JsonObject? diff,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetType);
        if (type.Length > TypeMaxLength || targetType.Length > TargetTypeMaxLength)
        {
            throw new ArgumentException("Type or target type too long.");
        }

        if (workspaceId == Guid.Empty || actorId == Guid.Empty || targetId == Guid.Empty)
        {
            throw new ArgumentException("Workspace, actor and target must be set.");
        }

        var at = timeProvider.GetUtcNow();
        return new ActivityEvent(Guid.CreateVersion7(at), workspaceId, type, actorId, targetType, targetId, spaceId, collectionId, diff ?? [], at);
    }
}

/// <summary>Event types of the activity log. Part of the API contract: add, never rename.</summary>
public static class ActivityTypes
{
    public const string RecordCreated = "record.created";
    public const string RecordUpdated = "record.updated";
    public const string RecordAppended = "record.appended";
    public const string RecordArchived = "record.archived";
    public const string RecordRestored = "record.restored";
    public const string SpaceCreated = "space.created";
    public const string SpaceUpdated = "space.updated";
    public const string SpaceArchived = "space.archived";
    public const string CollectionCreated = "collection.created";
    public const string CollectionUpdated = "collection.updated";
    public const string CollectionSchemaChanged = "collection.schema_changed";
    public const string CollectionArchived = "collection.archived";
    public const string MemberAdded = "member.added";
    public const string MemberUpdated = "member.updated";
    public const string MemberRemoved = "member.removed";

    public static IReadOnlyList<string> All { get; } =
    [
        RecordCreated, RecordUpdated, RecordAppended, RecordArchived, RecordRestored,
        SpaceCreated, SpaceUpdated, SpaceArchived,
        CollectionCreated, CollectionUpdated, CollectionSchemaChanged, CollectionArchived,
        MemberAdded, MemberUpdated, MemberRemoved,
    ];
}

/// <summary>Target types of activity events.</summary>
public static class ActivityTargets
{
    public const string Record = "record";
    public const string Space = "space";
    public const string Collection = "collection";

    /// <summary>A membership; the target id is the member's actor id.</summary>
    public const string Member = "member";
}
