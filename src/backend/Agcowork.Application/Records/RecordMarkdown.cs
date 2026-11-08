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
/// With resolved relations (<c>expandRelations</c>, AP-037) every referenced id gets its title as a YAML comment on the same
/// line, so the frontmatter stays valid YAML with the same values: <c>affects: 0190f5e2-… # "Persistence" (archived)</c>;
/// an id that is not available gets <c># (not available)</c>. Backlinks (<see cref="AppendBacklinks"/>) follow the body.
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
    /// <param name="relations">Resolved relation values (<c>expandRelations</c>): titles are added as comments next to the ids.</param>
    public static string Render(
        RecordDto record,
        string? collectionName,
        string spaceName,
        IReadOnlyDictionary<string, IReadOnlyList<RelationTargetDto>>? relations = null)
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
        // Resolved targets by id, once per property (one lookup per value instead of a scan).
        var targetsByKey = relations?.ToDictionary(
            r => r.Key,
            r => r.Value.DistinctBy(t => t.Id).ToDictionary(t => t.Id),
            StringComparer.Ordinal);
        foreach (var (key, value) in record.Properties)
        {
            // A computed property named like the fixed field it mirrors (e.g. created_at) would only repeat it.
            if (fixedValues.TryGetValue(key, out var same) && value.ValueKind == JsonValueKind.String && Inline(value) == same)
            {
                continue;
            }

            Entry(yaml, FixedKeys.Contains(key) ? $"properties.{key}" : key, value, targetsByKey?.GetValueOrDefault(key));
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

    /// <summary>
    /// The record's Markdown followed by a separator and a <c># Backlinks</c> section: per group the source collection and
    /// property, its records (id, quoted title, archived) and, when there are more, the records_query call that lists all.
    /// </summary>
    public static string AppendBacklinks(string recordMarkdown, RecordBacklinksDto backlinks)
    {
        ArgumentNullException.ThrowIfNull(recordMarkdown);
        ArgumentNullException.ThrowIfNull(backlinks);
        var text = new StringBuilder();
        text.Append(recordMarkdown.TrimEnd()).Append("\n\n---\n\n# Backlinks\n\n");
        if (backlinks.Groups.Count == 0 && backlinks.NextGroupsCursor is null)
        {
            text.Append("No active records reference this record.\n");
            return text.ToString();
        }

        text.Append("Records that reference this record through a relation, by collection and property, newest first.\n");
        if (backlinks.NextGroupsCursor is not null)
        {
            text.Append(CultureInfo.InvariantCulture, $"Only the first {Backlinks.MaxGroups} relation properties pointing here were looked at; more exist.\n");
        }

        foreach (var group in backlinks.Groups)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n- collection {OneLine(group.CollectionName)} (id {Id(group.CollectionId)}), property {group.PropertyKey} ({OneLine(group.PropertyName)}):\n");
            foreach (var item in group.Items)
            {
                text.Append(CultureInfo.InvariantCulture, $"  - {Id(item.Id)} {OneLine(item.Title)}{(item.Archived ? " (archived)" : string.Empty)}\n");
            }

            if (group.More is { } more)
            {
                text.Append(CultureInfo.InvariantCulture, $"  - more: records_query with collectionId {Id(more.CollectionId)} and filter ")
                    .Append(CultureInfo.InvariantCulture, $"{{ \"prop\": \"{more.Filter.Prop}\", \"op\": \"{more.Filter.Op}\", \"value\": \"{Id(more.Filter.Value)}\" }}\n");
            }
        }

        return text.ToString();
    }

    private static void Entry(StringBuilder yaml, string key, JsonElement value, IReadOnlyDictionary<Guid, RelationTargetDto>? targets)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            Scalar(yaml, key, Inline(value) + TitleComment(value, targets));
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
            yaml.Append("  - ").Append(Inline(item)).Append(TitleComment(item, targets)).Append('\n');
        }
    }

    /// <summary>
    /// <c> # "Title"</c> for a referenced id (with <c>(archived)</c>), <c> # (not available)</c> for an unknown one; empty
    /// without resolved relations. A YAML comment, so values stay unchanged; the title is one quoted line.
    /// </summary>
    private static string TitleComment(JsonElement value, IReadOnlyDictionary<Guid, RelationTargetDto>? targets)
    {
        if (targets is null || value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out var id)
            || !targets.TryGetValue(id, out var target))
        {
            return string.Empty;
        }

        return target.Title is null
            ? " # (not available)"
            : $" # {OneLine(target.Title)}{(target.Archived ? " (archived)" : string.Empty)}";
    }

    /// <summary>
    /// A free text as one double-quoted line (JSON escapes). Line and paragraph separators, which YAML 1.1 and some renderers
    /// treat as line breaks, are escaped too, so a title can never end a comment or start a new key.
    /// </summary>
    private static string OneLine(string value) =>
        JsonSerializer.Serialize(value, QuoteOptions).Replace("\u2028", "\\u2028", StringComparison.Ordinal).Replace("\u2029", "\\u2029", StringComparison.Ordinal).Replace("\u0085", "\\u0085", StringComparison.Ordinal);

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
