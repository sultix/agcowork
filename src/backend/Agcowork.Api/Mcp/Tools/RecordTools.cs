using System.ComponentModel;
using System.Text.Json;
using Agcowork.Application.Comments;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Query;
using Agcowork.Application.Records;
using Agcowork.Application.Views;
using Agcowork.Domain.Views;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agcowork.Api.Mcp.Tools;

/// <summary>Records: list, query, read, write, history (<c>/w/{workspaceSlug}/mcp</c>).</summary>
[McpServerToolType]
internal static class RecordTools
{
    public const string FormatMarkdown = "markdown";
    public const string FormatJson = "json";

    /// <summary>The use case names the field after the REST header; on MCP it is the argument <c>idempotencyKey</c>.</summary>
    private const string IdempotencyKeyRestField = "Idempotency-Key";

    private static readonly string[] Formats = [FormatMarkdown, FormatJson];

    [McpServerTool(Name = "records_list", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Simple list of the records of a collection (collectionId) or of the free pages of a space (spaceId), exactly one of both, "
        + "oldest first, with 'body': null. For filtering and sorting use records_query. Paginated: 'limit' 1..200 (default 50); pass "
        + "'nextCursor' of the previous page as 'cursor' (null on the last page). Requires read.")]
    public static async Task<CallToolResult> List(
        ListRecords useCase,
        McpOutput output,
        [Description("Records of this collection. Either this or spaceId.")] Guid? collectionId = null,
        [Description("Free pages (records without collection) of this space.")] Guid? spaceId = null,
        [Description("Also list archived records. Default false.")] bool? includeArchived = null,
        [Description("Page size 1..200, default 50.")] int? limit = null,
        [Description("nextCursor of the previous page, unchanged.")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var result = await useCase.HandleAsync(new ListRecordsRequest(collectionId, spaceId, includeArchived ?? false, limit, cursor), cancellationToken);
        return output.From(result, page => McpText.Records(page.Items, page.NextCursor));
    }

    [McpServerTool(Name = "records_query", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Filters, sorts and pages the records of a collection; the main way to find records. Call collection_describe first to "
        + "know the property keys and option values. 'sorts': [ { \"prop\", \"dir\": \"asc\"|\"desc\" } ] (empty values last; "
        + "multi_select, relation and json cannot be sorted). 'fields': property keys to return; add \"body\" for the Markdown body. "
        + "Results: 'items', 'nextCursor' (pass as 'cursor' for the next page, with the same filter and sorts; null when done) and, with "
        + "includeCount: true, 'count'. Invalid input comes back as errors[] on the path (e.g. 'filter.and[1].op') with 'allowed' "
        + "values. Requires read. {{filterDsl}}")]
    public static async Task<CallToolResult> Query(
        ExecuteQuery useCase,
        McpOutput output,
        [Description("Collection whose records are queried.")] Guid collectionId,
        [Description("Filter DSL node, see the tool description. Omit for all records.")] JsonElement? filter = null,
        [Description("Sort criteria in priority order (max. 10): [ { \"prop\": \"<key>\", \"dir\": \"asc\" | \"desc\" } ].")] IReadOnlyList<ViewSortInput>? sorts = null,
        [Description("Property keys to return (default: all); \"body\" adds the Markdown body.")] IReadOnlyList<string>? fields = null,
        [Description("Page size 1..500, default 50.")] int? limit = null,
        [Description("nextCursor of the previous page, unchanged.")] string? cursor = null,
        [Description("Also return archived records. Default false.")] bool? includeArchived = null,
        [Description("Also count all matches (up to 10000). Default false.")] bool? includeCount = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryReadFilter(output, filter, "filter", out var node, out var invalid))
        {
            return invalid;
        }

        var query = new QueryDefinition(node, sorts, limit, cursor, fields, includeArchived ?? false, includeCount ?? false);
        var result = await useCase.HandleAsync(new ExecuteQueryRequest(collectionId, query), cancellationToken);
        return output.From(result, page => McpText.Records(page.Items, page.NextCursor, page.Count));
    }

    [McpServerTool(Name = "record_get", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Reads one record, archived ones included. Default format 'markdown': YAML frontmatter (id, collection, space, title, all "
        + "properties, revision, timestamps, actors) followed by the Markdown body, the easiest form to read. format 'json' returns "
        + "the record object (id, title, properties, body, revision, …) and allows includeHistory. Note 'revision': send it as "
        + "'expectedRevision' to record_update so you do not overwrite a concurrent change. includeComments: true adds the "
        + "discussion (threads with replies, as comments_list) in both formats. Relation properties store only record ids: "
        + "expandRelations: true adds their titles (markdown: as a comment next to each id, e.g. 'affects: <id> # \"Billing\"'; "
        + "structured result 'relations': { key: [ { id, title, archived } ] }), so you need no extra call per id. "
        + "includeBacklinks: true answers 'who references this record?': the records whose relation properties point here, "
        + "grouped by collection and property (up to 50 groups), the newest 10 active ones per group (markdown: a '# Backlinks' "
        + "section after the body; structured: 'backlinks.groups'). A group with more has 'more': the collectionId and filter "
        + "to pass to records_query to list all of them (filter { \"prop\": <key>, \"op\": \"contains\", \"value\": <this "
        + "record id> }). Requires read.")]
    public static async Task<CallToolResult> Get(
        GetRecord getRecord,
        GetRecordMarkdown getMarkdown,
        ListComments listComments,
        McpOutput output,
        [Description("Record id.")] Guid recordId,
        [Description("'markdown' (default) or 'json'.")] string? format = null,
        [Description("json only: include the Markdown body. Default true.")] bool? includeBody = null,
        [Description("json only: attach the latest n revisions (0..100). Default 0.")] int? includeHistory = null,
        [Description("Attach the comments (threads with replies). Default false.")] bool? includeComments = null,
        [Description("Resolve relation values to the titles of the referenced records. Default false.")] bool? expandRelations = null,
        [Description("Attach the records that reference this one (first 10 per collection and property). Default false.")] bool? includeBacklinks = null,
        CancellationToken cancellationToken = default)
    {
        format ??= FormatMarkdown;
        if (!Formats.Contains(format, StringComparer.Ordinal))
        {
            return output.InvalidInput("format", ErrorCodes.NotInOptions, $"'{format}' is not a valid format.", Formats);
        }

        if (format == FormatMarkdown)
        {
            var rendered = await getMarkdown.RenderAsync(
                new GetRecordMarkdownRequest(recordId, ExpandRelations: expandRelations ?? false, IncludeBacklinks: includeBacklinks ?? false), cancellationToken);
            if (rendered.IsFailure)
            {
                return output.Failure(rendered.Error);
            }

            var view = rendered.Value;
            if (includeComments != true)
            {
                return output.Success(new RecordMarkdownResult(recordId, FormatMarkdown, view.Markdown, Relations: view.Relations, Backlinks: view.Backlinks), view.Markdown);
            }

            var comments = await listComments.HandleAsync(new ListCommentsRequest(recordId), cancellationToken);
            return comments.IsSuccess
                ? output.Success(
                    new RecordMarkdownResult(recordId, FormatMarkdown, view.Markdown, comments.Value, view.Relations, view.Backlinks),
                    CommentText.AppendTo(view.Markdown, comments.Value))
                : output.Failure(comments.Error);
        }

        var result = await getRecord.HandleAsync(
            new GetRecordRequest(recordId, includeBody ?? true, includeHistory ?? 0, includeComments ?? false, expandRelations ?? false, includeBacklinks ?? false),
            cancellationToken);
        return output.From(result, record => output.SummaryWithJson($"Record {record.Title} (id {record.Id}, revision {record.Revision}).", record));
    }

    [McpServerTool(Name = "record_create", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Creates a record in a collection (collectionId) or a free page in a space (spaceId). Call collection_describe first: "
        + "'properties' must follow its schema (keys, types, allowed option values); omitted properties get their default. The "
        + "collection's default template (or 'templateId') fills preset values and the body when you send none, e.g. the ADR "
        + "structure of 'Entscheidungen'; values you send win. 'body' is Markdown. Send 'idempotencyKey' (e.g. a UUID) so a retry "
        + "within 24 h returns the first record instead of creating a duplicate; a retry must repeat the call unchanged, the same "
        + "key with other arguments is 'idempotency_key_reused' (use a new key for every new record). Invalid values come back as errors[] on "
        + "'properties.<key>' (e.g. 'unknown_property' or 'not_in_options' with 'allowed'). Returns the record with 'body': null; read "
        + "it with record_get. Requires write.")]
    public static async Task<CallToolResult> Create(
        CreateRecord useCase,
        McpOutput output,
        [Description("Collection of the record (from workspace_overview). Either this or spaceId.")] Guid? collectionId = null,
        [Description("Space of a free page (no collection). With a collection it may be omitted.")] Guid? spaceId = null,
        [Description("Title, max. 500 characters. Required unless the template provides one.")] string? title = null,
        [Description("Property values by key, e.g. { \"status\": \"akzeptiert\", \"date\": \"2026-11-05\" }. Relation values are record ids, actor values member ids.")] IReadOnlyDictionary<string, JsonElement>? properties = null,
        [Description("Markdown body, max. 2 MiB. Omit to use the template's body.")] string? body = null,
        [Description("Template of the collection (templateId from collection_describe); default: the collection's default template.")] Guid? templateId = null,
        [Description("Parent record for nested pages.")] Guid? parentRecordId = null,
        [Description("Makes retries safe: the same key by you within 24 h with the same arguments returns the first record; with other arguments it is 'idempotency_key_reused'. 1-255 characters, e.g. a UUID.")] string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var result = await useCase.HandleAsync(
            new CreateRecordRequest(collectionId, spaceId, title, properties, body, templateId, parentRecordId, idempotencyKey), cancellationToken);
        return result.IsSuccess
            ? output.Success(result.Value, McpText.RecordChanged("Created", result.Value))
            : output.Failure(RenameField(result.Error, IdempotencyKeyRestField, "idempotencyKey"));
    }

    [McpServerTool(Name = "record_update", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Changes a record. 'properties' is a patch: only sent keys change, null removes a value (not for required properties). "
        + "'title' and 'body' replace the old values when sent; to add text use record_append instead of sending the whole body. "
        + "Use 'expectedRevision' from record_get: if the record moved on, you get 'revision_mismatch' with 'currentRevision' and "
        + "'changedKeys'; re-read the record, then retry. Without expectedRevision the last writer wins. 'reason' is stored with "
        + "the revision. Archived records cannot be changed ('record_archived'). Requires write.")]
    public static async Task<CallToolResult> Update(
        UpdateRecord useCase,
        McpOutput output,
        [Description("Record to change.")] Guid recordId,
        [Description("New title, max. 500 characters.")] string? title = null,
        [Description("Patch of property values by key; null removes a value.")] IReadOnlyDictionary<string, JsonElement>? properties = null,
        [Description("New Markdown body (replaces the whole body).")] string? body = null,
        [Description("The 'revision' you read; the change applies only if the record is still at it.")] int? expectedRevision = null,
        [Description("Why; stored with the revision.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var result = await useCase.HandleAsync(new UpdateRecordRequest(recordId, title, properties, body, expectedRevision, reason), cancellationToken);
        return output.From(result, record => McpText.RecordChanged("Updated", record));
    }

    [McpServerTool(Name = "record_append", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Appends Markdown to the body of a record without knowing or sending the body. This is the preferred way to add log-like "
        + "entries, findings, consequences or meeting notes. With 'section' (a heading text such as 'Log' or 'Konsequenzen', "
        + "case-insensitive, leading '#' optional) the text goes to the end of that section; a missing section is created as "
        + "'## <section>' at the end. Without 'section' it goes to the end of the body. Concurrent appends never overwrite each "
        + "other; no expectedRevision needed. Requires write.")]
    public static async Task<CallToolResult> Append(
        AppendToRecord useCase,
        McpOutput output,
        [Description("Record to append to.")] Guid recordId,
        [Description("Markdown to append, e.g. '- 2026-11-07: deployed v1.2'.")] string markdown,
        [Description("Heading to append under, e.g. 'Log'; created when missing. Omit for the end of the body.")] string? section = null,
        [Description("Why; stored with the revision.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var result = await useCase.HandleAsync(new AppendToRecordRequest(recordId, markdown, section, reason), cancellationToken);
        return output.From(result, record => McpText.RecordChanged("Appended", record));
    }

    [McpServerTool(Name = "record_archive", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Archives a record (soft delete): it stays readable with record_get but is hidden from lists and queries and read-only. "
        + "Records are never deleted. Already archived: 'record_archived'. Requires write.")]
    public static async Task<CallToolResult> Archive(
        ArchiveRecord useCase,
        McpOutput output,
        [Description("Record to archive.")] Guid recordId,
        [Description("Why; stored with the revision.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var result = await useCase.HandleAsync(new ArchiveRecordRequest(recordId, reason), cancellationToken);
        return output.From(result, record => McpText.RecordChanged("Archived", record));
    }

    [McpServerTool(Name = "history_get", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "The revisions of a record, newest first: number, kind (created, updated, appended, archived, restored), actor, time and "
        + "reason. Use it to see who changed what and why. Paginated with 'limit' (1..200, default 50) and 'cursor'. For "
        + "workspace-wide changes use activity_recent. Requires read.")]
    public static async Task<CallToolResult> History(
        GetRecordHistory useCase,
        McpOutput output,
        [Description("Record whose revisions to list.")] Guid recordId,
        [Description("Page size 1..200, default 50.")] int? limit = null,
        [Description("nextCursor of the previous page, unchanged.")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var result = await useCase.HandleAsync(new GetRecordHistoryRequest(recordId, limit, cursor), cancellationToken);
        return output.From(result, page => McpText.History(page.Items, page.NextCursor));
    }

    /// <summary>Renames a field of a validation error or conflict (adapter naming); other errors pass unchanged.</summary>
    private static Error RenameField(Error error, string from, string to) => error switch
    {
        ValidationError validation => new ValidationError([.. validation.Errors.Select(e => e.Field == from
            ? e with { Field = to, Message = e.Message.Replace("The Idempotency-Key header", "'idempotencyKey'", StringComparison.Ordinal) }
            : e)]),
        ConflictError { Field: { } field } conflict when field == from => conflict with { Field = to },
        _ => error,
    };

    /// <summary>Reads a filter argument into the DSL node; a malformed node is reported like a REST binding error, as a field error.</summary>
    internal static bool TryReadFilter(McpOutput output, JsonElement? filter, string field, out ViewFilterNode? node, out CallToolResult invalid)
    {
        node = null;
        invalid = null!;
        if (filter is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            invalid = output.InvalidInput(field, ErrorCodes.InvalidFormat, "The filter must be an object: { \"and\" | \"or\": [ … ] }, { \"not\": { … } } or { \"prop\", \"op\", \"value\" }.");
            return false;
        }

        try
        {
            node = element.Deserialize<ViewFilterNode>(output.Json);
            return true;
        }
        catch (JsonException exception)
        {
            invalid = output.InvalidInput(field, ErrorCodes.InvalidFormat, $"The filter is not valid filter DSL JSON: {exception.Message}");
            return false;
        }
    }
}

/// <summary>Structured result of <c>record_get</c> in Markdown format.</summary>
/// <param name="Id">Record id.</param>
/// <param name="Format">Always <c>markdown</c>.</param>
/// <param name="Markdown">Frontmatter plus body.</param>
/// <param name="Comments">The discussion; only with <c>includeComments</c>.</param>
/// <param name="Relations">Relation values resolved to { id, title, archived } per property; only with <c>expandRelations</c>.</param>
/// <param name="Backlinks">Records that reference this one, grouped by collection and property; only with <c>includeBacklinks</c>.</param>
internal sealed record RecordMarkdownResult(
    Guid Id,
    string Format,
    string Markdown,
    CommentThreadsDto? Comments = null,
    IReadOnlyDictionary<string, IReadOnlyList<RelationTargetDto>>? Relations = null,
    RecordBacklinksDto? Backlinks = null);
