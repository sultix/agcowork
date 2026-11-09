using System.Globalization;
using System.Text;
using System.Text.Json;
using Agcowork.Application.Activity;
using Agcowork.Application.Collections;
using Agcowork.Application.Comments;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Paging;
using Agcowork.Application.Records;
using Agcowork.Application.Search;
using Agcowork.Application.Views;
using Agcowork.Application.Workspaces;

namespace Agcowork.Api.Mcp;

/// <summary>Readable text blocks of tool results (the structured JSON is always attached as well).</summary>
internal static class McpText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// The workspace overview as Markdown: one section per space, one line per collection; with <paramref name="members"/> a
    /// members section with the actor ids to mention, and the hint to the inbox (AP-029).
    /// </summary>
    public static string Overview(WorkspaceOverviewDto overview, bool includeWorkspace = true, IReadOnlyList<MemberDto>? members = null)
    {
        ArgumentNullException.ThrowIfNull(overview);
        var text = new StringBuilder();
        if (includeWorkspace)
        {
            var w = overview.Workspace;
            text.Append(Invariant, $"# Workspace {QuotedString.Of(w.Name)} (slug {w.Slug}, id {w.Id})\n");
            if (!string.IsNullOrWhiteSpace(w.Description))
            {
                text.Append(Invariant, $"Description: {OneLine(w.Description)}\n");
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
            text.Append(Invariant, $"\n## {(space.Icon is null ? string.Empty : OneLine(space.Icon) + " ")}{QuotedString.Of(space.Name)} (spaceId {space.Id})\n");
            if (!string.IsNullOrWhiteSpace(space.Description))
            {
                text.Append(Invariant, $"Description: {OneLine(space.Description)}\n");
            }

            foreach (var collection in space.Collections)
            {
                text.Append(Invariant, $"- {QuotedString.Of(collection.Name)} (collectionId {collection.Id}, {collection.RecordCount} records): {OneLine(collection.Description)}\n");
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

        if (members is not null)
        {
            text.Append("\n## Members\nMention one in a comment with [@Name](actor:<actorId>). mentions_list shows where others mentioned you: the open questions to you.\n");
            foreach (var member in members)
            {
                text.Append(Invariant, $"- {QuotedString.Of(member.Name)} ({CommentText.Wire(member.Kind)}, {CommentText.Wire(member.Role)}, actorId {member.ActorId})\n");
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Free text (descriptions, text values) on one line, so it cannot break headings, list items or fake sections. Descriptions on
    /// their own line carry the prefix "Description: ", so even a text that starts with <c>#</c>, <c>-</c> or <c>&gt;</c> is no structure.
    /// </summary>
    private static string OneLine(string value) => value.ReplaceLineEndings(" ");

    /// <summary>Confirmation of a comment write: what happened, plus the comment.</summary>
    public static string CommentChanged(string what, CommentDto comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        return $"{what}: comment {comment.Id} on record {comment.RecordId}.\n\n{CommentText.Comment(comment, indent: string.Empty)}";
    }

    /// <summary>The threads of a record (<see cref="CommentText.Threads"/>).</summary>
    public static string Comments(CommentThreadsDto threads) => CommentText.Threads(threads);

    /// <summary>The caller's mentions, newest first.</summary>
    public static string Mentions(MentionPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var text = new StringBuilder();
        text.Append(Invariant, $"{page.Items.Count} mention(s), newest first; {page.UnreadCount} unread in total");
        text.Append(page.NextCursor is null ? "; no further pages.\n" : $"; more with cursor \"{page.NextCursor}\".\n");
        foreach (var m in page.Items)
        {
            var title = QuotedString.Of(m.RecordTitle);
            var thread = m.ParentCommentId is { } root ? $"reply in thread {root}" : "thread root";
            text.Append(Invariant, $"\n- {(m.ReadAt is null ? "UNREAD" : "read")} mention {m.Id} by {QuotedString.Of(m.ByActorName)} (actor {m.ByActorId}) at {m.CreatedAt:O} ");
            text.Append(Invariant, $"in comment {m.CommentId} ({thread}) on record {title} ({m.RecordId})\n");
            if (m.Excerpt.Length > 0)
            {
                text.Append("  ").Append(m.Excerpt).Append('\n');
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
        text.Append(Invariant, $"- {QuotedString.Of(record.Title)} (id {record.Id}, revision {record.Revision}");
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
        text.Append(Invariant, $"# Collection {QuotedString.Of(c.Name)} (collectionId {c.Id}, space {QuotedString.Of(description.Space.Name)}, schemaVersion {c.SchemaVersion}, {description.Statistics.RecordCount} records)\n");
        text.Append(string.IsNullOrWhiteSpace(c.Description) ? string.Empty : $"Description: {OneLine(c.Description)}\n");
        text.Append("\n## Properties\n");
        foreach (var p in c.Schema)
        {
            text.Append(Invariant, $"- `{p.Key}` ({JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Type.ToString())}{(p.Required ? ", required" : string.Empty)}{(p.ReadOnly ? ", read-only" : string.Empty)})");
            if (!string.IsNullOrWhiteSpace(p.Description))
            {
                text.Append(Invariant, $": {OneLine(p.Description)}");
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
            text.Append(Invariant, $"- {QuotedString.Of(t.Name)} (templateId {t.Id}{(t.IsDefault ? ", default: used by record_create unless templateId or skipTemplate=true is sent" : string.Empty)}){(string.IsNullOrWhiteSpace(t.Description) ? string.Empty : ": " + OneLine(t.Description))}\n");
        }

        text.Append("\n## Views\n");
        foreach (var v in views)
        {
            text.Append(Invariant, $"- {QuotedString.Of(v.Name)} ({v.Type}, viewId {v.Id}{(v.IsDefault ? ", default" : string.Empty)})\n");
        }

        text.Append(Invariant, $"\n## Example properties\n```json\n{description.Example.ToJsonString(json)}\n```\n");
        text.Append(Invariant, $"\n## JSON Schema of 'properties' (also resource {WorkspaceResources.SchemaUri(c.Id)})\n```json\n{description.JsonSchema.ToJsonString(json)}\n```\n");
        return text.ToString();
    }

    /// <summary>Search hits, best first: title, ids, rank and the snippet with its markers.</summary>
    public static string SearchHits(Page<SearchHitDto> page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var text = new StringBuilder();
        text.Append(Invariant, $"{page.Items.Count} hit(s), best first");
        text.Append(page.NextCursor is null ? "; no further hits.\n" : $"; more with cursor \"{page.NextCursor}\".\n");
        foreach (var hit in page.Items)
        {
            text.Append(Invariant, $"\n- {QuotedString.Of(hit.Title)} (recordId {hit.RecordId}, ");
            text.Append(hit.CollectionId is { } collectionId ? $"collectionId {collectionId}" : $"free page in spaceId {hit.SpaceId}");
            text.Append(Invariant, $", rank {hit.Rank:0.###}{(hit.Archived ? ", archived" : string.Empty)})\n");
            if (hit.Snippet.Length > 0)
            {
                text.Append("  ").Append(hit.Snippet.ReplaceLineEndings(" ")).Append('\n');
            }
        }

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
            text.Append(Invariant, $"- #{r.No} {JsonNamingPolicy.SnakeCaseLower.ConvertName(r.Kind.ToString())} by actor {r.ActorId} at {r.At:O}{(r.Reason is null ? string.Empty : $": {QuotedString.Of(r.Reason)}")}\n");
        }

        return text.ToString();
    }

    /// <summary>Activity events newest first.</summary>
    public static string Activity(ActivityPage page, bool forward)
    {
        ArgumentNullException.ThrowIfNull(page);
        var events = page.Items;
        var text = new StringBuilder();
        if (forward)
        {
            text.Append(Invariant, $"{events.Count} event(s) in commit-safe order (transaction order); continue with afterSeq {page.LastSeq} (lastSeq, not an item's seq).\n");
        }
        else
        {
            text.Append(Invariant, $"{events.Count} event(s), newest first{(page.NextCursor is null ? "; no further pages" : $"; older ones with cursor \"{page.NextCursor}\"")}; ");
            text.Append(Invariant, $"to follow new events from here, read forward with afterSeq {page.LastSeq}.\n");
        }

        foreach (var e in events)
        {
            // Titles are user and agent input: JSON-quoted, so quotes and line breaks cannot break the one-line format.
            var title = e.TargetTitle is null ? string.Empty : " " + QuotedString.Of(e.TargetTitle);
            text.Append(Invariant, $"- seq {e.Seq} {e.At:O} {e.Type} {e.TargetType}{title} {e.TargetId} by actor {e.ActorId}\n");
        }

        return text.ToString();
    }

    private static string Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => OneLine(value.GetString() ?? string.Empty),
        JsonValueKind.Array => "[" + string.Join(", ", value.EnumerateArray().Select(Value)) + "]",
        _ => value.GetRawText(),
    };
}
