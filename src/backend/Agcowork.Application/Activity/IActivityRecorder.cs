using System.Text.Json.Nodes;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;

namespace Agcowork.Application.Activity;

/// <summary>
/// Writes activity events. Use cases call <see cref="Record"/> after the change and before their single
/// <c>SaveChanges</c>, so the event is stored in the same transaction as the change, or not at all.
/// The actor is the authenticated caller (<see cref="ICurrentActor"/>), the workspace the current tenant.
/// </summary>
public interface IActivityRecorder
{
    /// <param name="type">One of <see cref="ActivityTypes"/>.</param>
    /// <param name="targetType">One of <see cref="ActivityTargets"/>.</param>
    /// <param name="targetId">Id of the target (for members: the actor id).</param>
    /// <param name="spaceId">Space of the target, for the <c>spaceId</c> filter.</param>
    /// <param name="collectionId">Collection of the target, for the <c>collectionId</c> filter.</param>
    /// <param name="diff">What changed; see <see cref="ActivityDiff"/> and <c>RecordDiff</c>.</param>
    /// <param name="relatedRecordId">
    /// For comment events the record of the comment (required); record events belong to their target anyway, other events to
    /// no record (see <see cref="ActivityEvent.RelatedRecordId"/>).
    /// </param>
    ActivityEvent Record(string type, string targetType, Guid targetId, Guid? spaceId = null, Guid? collectionId = null, JsonObject? diff = null, Guid? relatedRecordId = null);
}

/// <summary>Adds the event to the context; the caller saves.</summary>
public sealed class ActivityRecorder(IAgcoworkDbContext db, ICurrentActor currentActor, ITenantContext tenant, TimeProvider timeProvider) : IActivityRecorder
{
    public ActivityEvent Record(string type, string targetType, Guid targetId, Guid? spaceId = null, Guid? collectionId = null, JsonObject? diff = null, Guid? relatedRecordId = null)
    {
        var activity = ActivityEvent.Create(tenant.WorkspaceId, type, currentActor.Id, targetType, targetId, spaceId, collectionId, diff, timeProvider, relatedRecordId);
        db.ActivityEvents.Add(activity);
        return activity;
    }
}

/// <summary>Builds diffs of non-record events: <c>{ "field": { "old": …, "new": … } }</c>, like record diffs.</summary>
public sealed class ActivityDiff
{
    private readonly JsonObject _diff = [];

    /// <summary>Adds <paramref name="key"/> with old and new value when they differ (by JSON text).</summary>
    public ActivityDiff Change(string key, JsonNode? oldValue, JsonNode? newValue)
    {
        if (!JsonNode.DeepEquals(oldValue, newValue))
        {
            _diff[key] = new JsonObject { ["old"] = oldValue?.DeepClone(), ["new"] = newValue?.DeepClone() };
        }

        return this;
    }

    /// <summary>Adds a plain value (e.g. the name of a created thing, or lists of keys).</summary>
    public ActivityDiff Set(string key, JsonNode? value)
    {
        _diff[key] = value?.DeepClone();
        return this;
    }

    public JsonObject ToJson() => (JsonObject)_diff.DeepClone();

    /// <summary>Array of strings, for key lists.</summary>
    public static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);
}
