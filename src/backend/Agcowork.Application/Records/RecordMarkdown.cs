using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agcowork.Application.Records;

/// <summary>
/// Markdown rendering of a record with YAML frontmatter (architecture §4.4). One direction only: frontmatter is not parsed back.
/// </summary>
/// <remarks>
/// Layout:
/// <code>
/// ---
/// id: 0190f5e2-…
/// collection: Entscheidungen        (collection name; free pages have no collection lines)
/// collection_id: 0190f5e2-…
/// space: Technik
/// space_id: 0190f5e2-…
/// title: PostgreSQL statt MongoDB
/// status: accepted                  (all properties in schema order)
/// tags:
///   - db
/// revision: 3
/// created_at: 2026-11-07T12:00:00Z
/// updated_at: 2026-11-07T12:30:00Z
/// created_by: 0190f5e2-…            (actor ids)
/// updated_by: 0190f5e2-…
/// parent_record_id: …               (only when nested)
/// archived_at: …                    (only when archived)
/// ---
/// &lt;body&gt;
/// </code>
/// Values: strings plain when unambiguous, otherwise double-quoted with JSON escapes (valid YAML); numbers and booleans
/// as is; arrays as block lists (items inline); objects (json properties) as JSON flow mappings. A property whose key
/// collides with one of the fixed keys above is written as <c>properties.&lt;key&gt;</c>; a computed property that repeats the
/// fixed field of the same name with the same value (e.g. <c>created_at</c>) is left out.
/// </remarks>
public static class RecordMarkdown
{
    private static readonly HashSet<string> FixedKeys = new(StringComparer.Ordinal)
    {
        "id", "collection", "collection_id", "space", "space_id", "title", "revision",
        "created_at", "updated_at", "created_by", "updated_by", "parent_record_id", "archived_at",
    };

    private static readonly JsonSerializerOptions QuoteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly string[] Reserved = ["true", "false", "yes", "no", "on", "off", "null", "y", "n", "~"];

    /// <param name="record">The record; its body is rendered after the frontmatter (null counts as empty).</param>
    /// <param name="collectionName">Name of the collection; null for free pages.</param>
    /// <param name="spaceName">Name of the space.</param>
    public static string Render(RecordDto record, string? collectionName, string spaceName)
    {
        ArgumentNullException.ThrowIfNull(record);

        var yaml = new StringBuilder("---\n");
        Scalar(yaml, "id", Id(record.Id));
        if (record.CollectionId is { } collectionId)
        {
            Scalar(yaml, "collection", Quote(collectionName ?? string.Empty));
            Scalar(yaml, "collection_id", Id(collectionId));
        }

        Scalar(yaml, "space", Quote(spaceName));
        Scalar(yaml, "space_id", Id(record.SpaceId));
        Scalar(yaml, "title", Quote(record.Title));
        var fixedValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["created_at"] = Time(record.CreatedAt),
            ["updated_at"] = Time(record.UpdatedAt),
            ["created_by"] = Id(record.CreatedBy),
            ["updated_by"] = Id(record.UpdatedBy),
        };
        foreach (var (key, value) in record.Properties)
        {
            // A computed property named like the fixed field it mirrors (e.g. created_at) would only repeat it.
            if (fixedValues.TryGetValue(key, out var same) && value.ValueKind == JsonValueKind.String && Inline(value) == same)
            {
                continue;
            }

            Entry(yaml, FixedKeys.Contains(key) ? $"properties.{key}" : key, value);
        }

        Scalar(yaml, "revision", record.Revision.ToString(CultureInfo.InvariantCulture));
        Scalar(yaml, "created_at", Time(record.CreatedAt));
        Scalar(yaml, "updated_at", Time(record.UpdatedAt));
        Scalar(yaml, "created_by", Id(record.CreatedBy));
        Scalar(yaml, "updated_by", Id(record.UpdatedBy));
        if (record.ParentRecordId is { } parentId)
        {
            Scalar(yaml, "parent_record_id", Id(parentId));
        }

        if (record.ArchivedAt is { } archivedAt)
        {
            Scalar(yaml, "archived_at", Time(archivedAt));
        }

        yaml.Append("---\n");
        yaml.Append(record.Body);
        return yaml.ToString();
    }

    /// <summary>A string as YAML scalar: plain when it cannot be misread, otherwise double-quoted.</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return IsPlainSafe(value) ? value : JsonSerializer.Serialize(value, QuoteOptions);
    }

    private static void Entry(StringBuilder yaml, string key, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            Scalar(yaml, key, Inline(value));
            return;
        }

        if (value.GetArrayLength() == 0)
        {
            Scalar(yaml, key, "[]");
            return;
        }

        yaml.Append(key).Append(":\n");
        foreach (var item in value.EnumerateArray())
        {
            yaml.Append("  - ").Append(Inline(item)).Append('\n');
        }
    }

    private static void Scalar(StringBuilder yaml, string key, string rendered) => yaml.Append(key).Append(": ").Append(rendered).Append('\n');

    private static string Inline(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Quote(value.GetString()!),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => "null",
        // JSON is a subset of YAML flow style.
        _ => JsonNode.Parse(value.GetRawText())!.ToJsonString(QuoteOptions),
    };

    private static string Id(Guid id) => id.ToString("D");

    private static string Time(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    private static bool IsPlainSafe(string value)
    {
        if (value.Length == 0 || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            return false;
        }

        if ("-?:,[]{}#&*!|>'\"%@`".Contains(value[0], StringComparison.Ordinal)
            || value.Any(char.IsControl)
            || value.Contains(": ", StringComparison.Ordinal)
            || value.Contains(" #", StringComparison.Ordinal)
            || value.EndsWith(':'))
        {
            return false;
        }

        return !Reserved.Contains(value, StringComparer.OrdinalIgnoreCase)
            && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }
}
