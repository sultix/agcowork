using System.Globalization;
using System.Text;
using System.Text.Json;
using Agcowork.Application.Activity;
using Agcowork.Application.Collections;
using Agcowork.Application.Records;
using Agcowork.Application.Views;
using Agcowork.Application.Workspaces;

namespace Agcowork.Api.Mcp;

/// <summary>Readable text blocks of tool results (the structured JSON is always attached as well).</summary>
internal static class McpText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The workspace overview as Markdown: one section per space, one line per collection.</summary>
    public static string Overview(WorkspaceOverviewDto overview, bool includeWorkspace = true)
    {
        ArgumentNullException.ThrowIfNull(overview);
        var text = new StringBuilder();
        if (includeWorkspace)
        {
            var w = overview.Workspace;
            text.Append(Invariant, $"# Workspace {w.Name} (slug {w.Slug}, id {w.Id})\n");
            if (!string.IsNullOrWhiteSpace(w.Description))
            {
                text.Append(Invariant, $"{w.Description}\n");
            }

            text.Append(Invariant, $"Your role: {w.Role.ToString().ToLowerInvariant()}; scopes: {string.Join(", ", w.Scopes)}");
            text.Append(w.ArchivedAt is null ? "\n" : "; ARCHIVED (read-only)\n");
            if (overview.Spaces.Count == 0)
            {
                text.Append("\nNo spaces yet. Create one with space_create.\n");
            }
        }

        foreach (var space in overview.Spaces)
        {
            text.Append(Invariant, $"\n## {(space.Icon is null ? string.Empty : space.Icon + " ")}{space.Name} (spaceId {space.Id})\n");
            if (!string.IsNullOrWhiteSpace(space.Description))
            {
                text.Append(Invariant, $"{space.Description}\n");
            }

            foreach (var collection in space.Collections)
            {
                text.Append(Invariant, $"- **{collection.Name}** (collectionId {collection.Id}, {collection.RecordCount} records): {collection.Description}\n");
            }

            if (space.Collections.Count == 0)
            {
                text.Append("- no collections\n");
            }

            if (space.PageCount > 0)
            {
                text.Append(Invariant, $"- {space.PageCount} free pages (records_list with spaceId)\n");
            }
        }

        return text.ToString();
    }

    /// <summary>Records as a list: title, id, revision and the property values in one line each.</summary>
    public static string Records(IReadOnlyList<RecordDto> records, string? nextCursor, int? count = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        var text = new StringBuilder();
        text.Append(Invariant, $"{records.Count} record(s)");
        if (count is { } total)
        {
            text.Append(Invariant, $" of {total} match(es)");
        }

        text.Append(nextCursor is null ? "; no further pages.\n" : $"; more with cursor \"{nextCursor}\".\n");
        foreach (var record in records)
        {
            text.Append('\n').Append(RecordLine(record));
        }

        return text.ToString();
    }

    /// <summary>One record in one line (body, if present, below it).</summary>
    public static string RecordLine(RecordDto record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var text = new StringBuilder();
        text.Append(Invariant, $"- **{record.Title}** (id {record.Id}, revision {record.Revision}");
        if (record.ArchivedAt is not null)
        {
            text.Append(", archived");
        }

        text.Append(')');
        var values = record.Properties
            .Where(p => p.Key != "title" && p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            .Select(p => $"{p.Key}={Value(p.Value)}")
            .ToList();
        if (values.Count > 0)
        {
            text.Append(": ").AppendJoin("; ", values);
        }

        text.Append('\n');
        if (record.Body is { Length: > 0 } body)
        {
            text.Append('\n').Append(body.TrimEnd()).Append("\n\n");
        }

        return text.ToString();
    }

    /// <summary>Confirmation of a write: what happened, plus the record line.</summary>
    public static string RecordChanged(string what, RecordDto record) =>
        $"{what}: {RecordLine(record).TrimStart('-', ' ')}Use revision {record.Revision.ToString(Invariant)} as expectedRevision for the next record_update.";

    /// <summary>Collection description: schema in readable form, templates, views, example.</summary>
    public static string Describe(CollectionDescriptionDto description, IReadOnlyList<ViewDto> views, JsonSerializerOptions json)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(views);
        var c = description.Collection;
        var text = new StringBuilder();
        text.Append(Invariant, $"# Collection {c.Name} (collectionId {c.Id}, space {description.Space.Name}, schemaVersion {c.SchemaVersion}, {description.Statistics.RecordCount} records)\n");
        text.Append(Invariant, $"{c.Description}\n\n## Properties\n");
        foreach (var p in c.Schema)
        {
            text.Append(Invariant, $"- `{p.Key}` ({JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Type.ToString())}{(p.Required ? ", required" : string.Empty)}{(p.ReadOnly ? ", read-only" : string.Empty)})");
            if (!string.IsNullOrWhiteSpace(p.Description))
            {
                text.Append(Invariant, $": {p.Description}");
            }

            if (p.Options is { Count: > 0 } options)
            {
                text.Append(Invariant, $" allowed: {string.Join(", ", options.Select(o => o.Value))}");
            }

            if (p.TargetCollectionId is { } target)
            {
                text.Append(Invariant, $" → records of collection {target}{(p.Multiple ? " (several)" : string.Empty)}");
            }

            text.Append('\n');
        }

        text.Append("\n## Templates\n");
        if (description.Templates.Count == 0)
        {
            text.Append("- none\n");
        }

        foreach (var t in description.Templates)
        {
            text.Append(Invariant, $"- {t.Name} (templateId {t.Id}{(t.IsDefault ? ", default: used by record_create unless templateId is sent" : string.Empty)}){(string.IsNullOrWhiteSpace(t.Description) ? string.Empty : ": " + t.Description)}\n");
        }

        text.Append("\n## Views\n");
        foreach (var v in views)
        {
            text.Append(Invariant, $"- {v.Name} ({v.Type}, viewId {v.Id}{(v.IsDefault ? ", default" : string.Empty)})\n");
        }

        text.Append(Invariant, $"\n## Example properties\n```json\n{description.Example.ToJsonString(json)}\n```\n");
        text.Append(Invariant, $"\n## JSON Schema of 'properties' (also resource {WorkspaceResources.SchemaUri(c.Id)})\n```json\n{description.JsonSchema.ToJsonString(json)}\n```\n");
        return text.ToString();
    }

    /// <summary>Revisions newest first.</summary>
    public static string History(IReadOnlyList<RevisionSummaryDto> revisions, string? nextCursor)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        var text = new StringBuilder();
        text.Append(Invariant, $"{revisions.Count} revision(s), newest first{(nextCursor is null ? string.Empty : $"; more with cursor \"{nextCursor}\"")}.\n");
        foreach (var r in revisions)
        {
            text.Append(Invariant, $"- #{r.No} {JsonNamingPolicy.SnakeCaseLower.ConvertName(r.Kind.ToString())} by actor {r.ActorId} at {r.At:O}{(r.Reason is null ? string.Empty : $": {r.Reason}")}\n");
        }

        return text.ToString();
    }

    /// <summary>Activity events newest first.</summary>
    public static string Activity(IReadOnlyList<ActivityEventDto> events, string? nextCursor)
    {
        ArgumentNullException.ThrowIfNull(events);
        var text = new StringBuilder();
        text.Append(Invariant, $"{events.Count} event(s), newest first{(nextCursor is null ? "; no further pages" : $"; older ones with cursor \"{nextCursor}\"")}.\n");
        foreach (var e in events)
        {
            text.Append(Invariant, $"- seq {e.Seq} {e.At:O} {e.Type} {e.TargetType} {e.TargetId} by actor {e.ActorId}\n");
        }

        return text.ToString();
    }

    private static string Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Array => "[" + string.Join(", ", value.EnumerateArray().Select(Value)) + "]",
        _ => value.GetRawText(),
    };
}
