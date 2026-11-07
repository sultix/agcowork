using System.Text.Json;

namespace Agcowork.Domain.Records;

/// <summary>The content of a record at one point in time: what a <see cref="Revision"/> stores and what diffs compare.</summary>
/// <param name="Title">Title of the record.</param>
/// <param name="Properties">Property values by key, without <c>title</c> and without nulls.</param>
/// <param name="Body">Markdown body.</param>
public sealed record RecordSnapshot(string Title, IReadOnlyDictionary<string, JsonElement> Properties, string Body)
{
    /// <summary>Same title, body and property values (values compared by their JSON text).</summary>
    public bool SameContentAs(RecordSnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(Title, other.Title, StringComparison.Ordinal)
            && string.Equals(Body, other.Body, StringComparison.Ordinal)
            && SameProperties(Properties, other.Properties);
    }

    public static bool SameProperties(IReadOnlyDictionary<string, JsonElement> a, IReadOnlyDictionary<string, JsonElement> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Count == b.Count
            && a.All(p => b.TryGetValue(p.Key, out var other) && SameValue(p.Value, other));
    }

    /// <summary>Structural equality of two JSON values (object key order does not matter).</summary>
    public static bool SameValue(JsonElement a, JsonElement b) => JsonElement.DeepEquals(a, b);
}
