using System.ComponentModel;
using System.Text.Json;
using Agcowork.Application.Activity;
using Agcowork.Application.Views;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agcowork.Api.Mcp.Tools;

/// <summary>Views for humans and the activity feed (<c>/w/{workspaceSlug}/mcp</c>).</summary>
[McpServerToolType]
internal static class ViewAndActivityTools
{
    [McpServerTool(Name = "view_create", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Creates a view of a collection for humans in the web app (agents read data with records_query). 'type' allowed: {{viewTypes}}. "
        + "A board needs 'groupBy': the key of a select or status property (its columns). "
        + "'sorts': [ { \"prop\", \"dir\": \"asc\"|\"desc\" } ]; 'visibleProperties': keys shown (empty = all); 'cardCoverProperty': "
        + "gallery cover; 'isDefault': true makes it the default view. All keys must exist in the schema (collection_describe). "
        + "Invalid input comes back as errors[] with 'allowed'. Requires write. {{filterDsl}}")]
    public static async Task<CallToolResult> Create(
        CreateView useCase,
        McpOutput output,
        [Description("Collection the view belongs to.")] Guid collectionId,
        [Description("Allowed: {{viewTypes}}.")] string type,
        [Description("Display name, unique in the collection, max. 100 characters, e.g. 'Board nach Status'.")] string name,
        [Description("Filter DSL node (same rules as records_query). Omit for all records.")] JsonElement? filter = null,
        [Description("Sort criteria: [ { \"prop\": \"<key>\", \"dir\": \"asc\" | \"desc\" } ].")] IReadOnlyList<ViewSortInput>? sorts = null,
        [Description("board: key of a select/status property; required for boards.")] string? groupBy = null,
        [Description("Keys of the properties shown; omit for all.")] IReadOnlyList<string>? visibleProperties = null,
        [Description("gallery: key of the property used as card cover.")] string? cardCoverProperty = null,
        [Description("Make this the collection's default view. Default false.")] bool? isDefault = null,
        CancellationToken cancellationToken = default)
    {
        if (!RecordTools.TryReadFilter(output, filter, "filter", out var node, out var invalid))
        {
            return invalid;
        }

        var result = await useCase.HandleAsync(
            new CreateViewRequest(collectionId, name, type, node, sorts, groupBy, visibleProperties, cardCoverProperty, isDefault ?? false),
            cancellationToken);
        return output.From(result, view => output.SummaryWithJson($"Created view {view.Name} ({view.Type}, viewId {view.Id}).", view));
    }

    [McpServerTool(Name = "activity_recent", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "What happened in the workspace: who created, changed, appended, archived or restored which record, space, collection or "
        + "member, with a diff ('old'/'new' per changed field). targetTitle is the current title (record) or name (space, "
        + "collection), null if the target no longer exists or is a member or view. Filters: spaceId, collectionId, actorId, "
        + "targetId (the history of one record, space or collection), since, types (allowed: {{activityTypes}}). "
        + "Two modes. Without afterSeq: newest first, paginated with 'limit' (1..200, default 50) and 'cursor'; for a look back. "
        + "With afterSeq: forward in commit-safe order (transaction order), only final events; to keep up with changes. Start "
        + "with afterSeq=0 (whole log) or with lastSeq of a newest-first call (from now on), then always call again with the "
        + "lastSeq of the previous result (full page: call again at once). Only lastSeq is a safe cursor: never pass the seq of an "
        + "item, it can skip events. This never skips an event, even when changes commit in another order than their seq: a lower "
        + "seq can follow a higher one, so keep lastSeq as given instead of computing the highest seq yourself. "
        + "Keep up with afterSeq, not with since: 'at' is taken before the commit, so an event can appear later with an 'at' "
        + "before your last poll. afterSeq and cursor do not combine. Requires read.")]
    public static async Task<CallToolResult> Recent(
        ListActivity useCase,
        McpOutput output,
        [Description("Only events in this space.")] Guid? spaceId = null,
        [Description("Only events in this collection.")] Guid? collectionId = null,
        [Description("Only events by this actor.")] Guid? actorId = null,
        [Description("Only events at or after this time, ISO 8601, e.g. 2026-11-06T00:00:00Z. For a one-off look back only; see the tool description.")] DateTimeOffset? since = null,
        [Description("Only events of these types (see the tool description).")] IReadOnlyList<string>? types = null,
        [Description("Page size 1..200, default 50.")] int? limit = null,
        [Description("Newest first: nextCursor of the previous page, unchanged.")] string? cursor = null,
        [Description("Forward reading: 0 for the whole log, otherwise lastSeq of the previous result, unchanged (never the seq of an item).")] long? afterSeq = null,
        [Description("Only events of this target: record, space, collection or view id; for members the actor id.")] Guid? targetId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await useCase.HandleAsync(
            new ListActivityRequest(spaceId, collectionId, actorId, since, types, limit, cursor, afterSeq, targetId),
            cancellationToken);
        return output.From(result, page => McpText.Activity(page, forward: afterSeq is not null));
    }
}
