using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agcowork.Domain.Records;

/// <summary>
/// Differences between two record snapshots, for the activity feed and for <c>revision_mismatch</c> conflicts.
/// </summary>
/// <remarks>
/// Diff format (JSON object, keys only when something changed):
/// <code>
/// {
///   "title":       { "old": "A", "new": "B" },
///   "properties":  { "status": { "old": "todo", "new": "done" }, "owner": { "old": null, "new": "…" } },
///   "bodyChanged": true,
///   "bodyDelta":   { "added": 12, "removed": 3 }
/// }
/// </code>
/// A property that was absent is <c>null</c> on that side. <c>bodyDelta</c> counts characters outside the common prefix and
/// suffix of old and new body: cheap, and exact for appends and single edits.
/// </remarks>
public static class RecordDiff
{
    public const string TitleKey = "title";
    public const string BodyKey = "body";

    public static JsonObject Between(RecordSnapshot before, RecordSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var diff = new JsonObject();
        if (!string.Equals(before.Title, after.Title, StringComparison.Ordinal))
        {
            diff[TitleKey] = Change(JsonValue.Create(before.Title), JsonValue.Create(after.Title));
        }

        var properties = new JsonObject();
        foreach (var key in ChangedPropertyKeys(before, after))
        {
            properties[key] = Change(Node(before.Properties, key), Node(after.Properties, key));
        }

        if (properties.Count > 0)
        {
            diff["properties"] = properties;
        }

        if (!string.Equals(before.Body, after.Body, StringComparison.Ordinal))
        {
            var (added, removed) = BodyDelta(before.Body, after.Body);
            diff["bodyChanged"] = true;
            diff["bodyDelta"] = new JsonObject { ["added"] = added, ["removed"] = removed };
        }

        return diff;
    }

    /// <summary>Diff of a new record: every value with <c>old: null</c>; the body as added characters.</summary>
    public static JsonObject Created(RecordSnapshot created)
    {
        ArgumentNullException.ThrowIfNull(created);
        var diff = Between(new RecordSnapshot(string.Empty, new Dictionary<string, JsonElement>(), string.Empty), created);
        if (diff[TitleKey] is JsonObject title)
        {
            title["old"] = null;
        }

        return diff;
    }

    /// <summary>What changed, as keys: <c>title</c>, property keys (ordinal order), <c>body</c>.</summary>
    public static IReadOnlyList<string> ChangedKeys(RecordSnapshot before, RecordSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var keys = new List<string>();
        if (!string.Equals(before.Title, after.Title, StringComparison.Ordinal))
        {
            keys.Add(TitleKey);
        }

        keys.AddRange(ChangedPropertyKeys(before, after));
        if (!string.Equals(before.Body, after.Body, StringComparison.Ordinal))
        {
            keys.Add(BodyKey);
        }

        return keys;
    }

    /// <summary>Characters added and removed between <paramref name="before"/> and <paramref name="after"/> (outside common prefix and suffix).</summary>
    public static (int Added, int Removed) BodyDelta(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var prefix = 0;
        var max = Math.Min(before.Length, after.Length);
        while (prefix < max && before[prefix] == after[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < max - prefix && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
        }

        return (after.Length - prefix - suffix, before.Length - prefix - suffix);
    }

    private static IEnumerable<string> ChangedPropertyKeys(RecordSnapshot before, RecordSnapshot after) =>
        before.Properties.Keys
            .Union(after.Properties.Keys, StringComparer.Ordinal)
            .Where(key => !(before.Properties.TryGetValue(key, out var a) && after.Properties.TryGetValue(key, out var b) && RecordSnapshot.SameValue(a, b)))
            .Order(StringComparer.Ordinal);

    private static JsonObject Change(JsonNode? oldValue, JsonNode? newValue) => new() { ["old"] = oldValue, ["new"] = newValue };

    private static JsonNode? Node(IReadOnlyDictionary<string, JsonElement> properties, string key) =>
        properties.TryGetValue(key, out var value) ? JsonNode.Parse(value.GetRawText()) : null;
}
